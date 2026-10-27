using System.Runtime.InteropServices;

namespace F1.Simulator;

/// <summary>
/// Procesador de telemetría: recibe eventos por HTTP, los procesa con una capacidad limitada y publica
/// las métricas de aplicación. La memoria que usa la cola y la "fuga" es memoria real (no administrada),
/// por eso al pasar el límite del contenedor Docker lo mata (OOMKilled) y se reinicia.
/// </summary>
public sealed class TelemetryProcessor : BackgroundService
{
    private const int EventBlockBytes = 256 * 1024;   // memoria que ocupa cada evento en cola
    private const int LeakMiBPerTick = 14;

    private static readonly KeyValuePair<string, object?> ServiceTag = Telemetry.Tag("service", "telemetry-processor");

    private readonly object _gate = new();
    private readonly Random _rnd = new();
    private readonly ILogger<TelemetryProcessor> _logger;
    private readonly Stack<IntPtr> _queueBlocks = new();
    private readonly List<IntPtr> _leakBlocks = new();
    private readonly DateTime _started = DateTime.UtcNow;

    private long _received;
    private long _backlog;
    private bool _freeze;
    private bool _leak;
    private int _slowTicks;

    public TelemetryProcessor(ILogger<TelemetryProcessor> logger) => _logger = logger;

    /// <summary>Llamado por el endpoint /ingest: solo cuenta los eventos que llegan.</summary>
    public void Enqueue(int events) => Interlocked.Add(ref _received, events);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Procesador de telemetría iniciado");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) Tick();
    }

    private void Tick()
    {
        lock (_gate)
        {
            var received = Interlocked.Exchange(ref _received, 0);
            _backlog += received;

            if (!_freeze)
            {
                var capacity = (long)(60 * (_slowTicks > 0 ? 0.35 : 1));
                var processed = Math.Min(_backlog, capacity);
                _backlog -= processed;

                if (processed > 0)
                {
                    var load = 1 + Math.Min(_backlog / 60.0, 6);
                    var slow = _slowTicks > 0 ? 8.0 : 1.0;
                    var burst = received > 40 ? 1.5 : 1.0;
                    for (var i = 0; i < 3; i++)
                        Telemetry.ProcessingLatency.Record((0.02 + _rnd.NextDouble() * 0.03) * load * slow * burst, ServiceTag);
                    Telemetry.EventsProcessed.Add(processed, ServiceTag);
                }
            }

            if (_slowTicks > 0 && --_slowTicks == 0) _logger.LogInformation("El procesador recupera su velocidad");

            SyncQueueMemory();
            if (_leak) _leakBlocks.Add(AllocateTouched(LeakMiBPerTick * 1024 * 1024));
        }
    }

    /// <summary>Mantiene un bloque de memoria real por cada evento en cola.</summary>
    private void SyncQueueMemory()
    {
        while (_queueBlocks.Count < _backlog) _queueBlocks.Push(AllocateTouched(EventBlockBytes));
        while (_queueBlocks.Count > _backlog) Marshal.FreeHGlobal(_queueBlocks.Pop());
    }

    /// <summary>Reserva memoria no administrada y la "toca" para que cuente de verdad en el contenedor.</summary>
    private static IntPtr AllocateTouched(int bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes);
        for (var offset = 0; offset < bytes; offset += 4096) Marshal.WriteByte(ptr, offset, 1);
        return ptr;
    }

    /// <summary>Memoria del contenedor según el cgroup: es la misma medida que ve cAdvisor y la que mata Docker.</summary>
    private static double ReadContainerMemoryMiB()
    {
        foreach (var path in new[] { "/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory/memory.usage_in_bytes" })
        {
            try
            {
                if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var bytes))
                    return Math.Round(bytes / 1048576.0, 1);
            }
            catch (IOException) { }
        }
        return Math.Round(Environment.WorkingSet / 1048576.0, 1);
    }

    public ProcessorStatus Status()
    {
        lock (_gate)
        {
            return new ProcessorStatus
            {
                Reachable = true,
                Frozen = _freeze,
                SlowTicks = _slowTicks,
                Leak = _leak,
                Backlog = _backlog,
                MemoryMiB = ReadContainerMemoryMiB(),
                UptimeSeconds = Math.Round((DateTime.UtcNow - _started).TotalSeconds, 1),
            };
        }
    }

    public void ToggleFreeze()
    {
        lock (_gate)
        {
            _freeze = !_freeze;
            _logger.LogWarning(_freeze ? "Procesador congelado: los eventos se acumulan en cola" : "Procesador reanudado: se vacía la cola");
        }
    }

    public void Slow()
    {
        lock (_gate)
        {
            _slowTicks = 12;
            _logger.LogWarning("Procesador lento: capacidad reducida");
        }
    }

    public void ToggleLeak()
    {
        lock (_gate)
        {
            _leak = !_leak;
            _logger.LogWarning(_leak ? "Fuga de memoria activada" : "Fuga de memoria detenida (la memoria ya perdida no se libera)");
        }
    }
}
