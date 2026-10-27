using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;

namespace F1.Simulator;

/// <summary>
/// Simula la carrera (3 pilotos), publica las métricas de negocio y envía eventos de telemetría
/// al procesador por HTTP. 1 tick = 1 segundo real = 5 segundos de carrera.
/// </summary>
public sealed class RaceEngine : BackgroundService
{
    private const double Dt = 5;
    private const double BaseLap = 90;
    private const int NitroTicks = 8;
    private const int NitroMin = 20;
    private const double NitroDrain = 6;
    private const double Regen = 0.9;
    private const double MemWarnMiB = 256 * 0.85;

    private readonly object _gate = new();
    private readonly Random _rnd = new();
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RaceEngine> _logger;
    private readonly List<LogEntry> _logs = new();
    private readonly HashSet<string> _sysFlags = new();

    private List<Driver> _drivers = NewDrivers();
    private ProcessorStatus _processor = new();
    private bool _processorSeen;
    private int _tick, _scTicks, _burstTicks;
    private bool _rain;

    public RaceEngine(IHttpClientFactory http, ILogger<RaceEngine> logger)
    {
        _http = http;
        _logger = logger;

        Observe("f1_speed_kmh", d => d.Speed, "Velocidad actual");
        Observe("f1_tyre_degradation_percent", d => d.Deg, "Degradación de ruedas");
        Observe("f1_fuel_remaining_kg", d => d.Fuel, "Combustible restante");
        Observe("f1_engine_temperature_celsius", d => d.Temp, "Temperatura del motor");
        Observe("f1_battery_charge_percent", d => d.Battery, "Carga de la batería");
    }

    private static List<Driver> NewDrivers() => new()
    {
        new Driver("VER", "red_bull", "#5794f2", 1.00, 1.00, 95),
        new Driver("HAM", "mercedes", "#73d6c9", 1.01, 1.05, 80),
        new Driver("LEC", "ferrari", "#f2495c", 1.005, 0.96, 88),
    };

    /// <summary>Crea las series de los contadores en cero para que Grafana muestre 0 en vez de "sin datos".</summary>
    private void InitSeries()
    {
        foreach (var d in _drivers)
            Telemetry.PitStops.Add(0, Telemetry.Tag("driver", d.Code), Telemetry.Tag("team", d.Team));
        Telemetry.Incidents.Add(0, Telemetry.Tag("type", "engine"));
        Telemetry.Incidents.Add(0, Telemetry.Tag("type", "safety_car"));
    }

    private void Observe(string name, Func<Driver, double> value, string description)
    {
        Telemetry.Meter.CreateObservableGauge<double>(name, () =>
        {
            lock (_gate)
            {
                return _drivers.Select(d => new Measurement<double>(
                    value(d),
                    new KeyValuePair<string, object?>("driver", d.Code),
                    new KeyValuePair<string, object?>("team", d.Team))).ToList();
            }
        }, description: description);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Acá ya arrancó el proveedor de métricas; si se hiciera en el constructor, estos ceros se perderían.
        InitSeries();
        Log("INFO", "Carrera iniciada: 3 pilotos en pista");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Step();
                await SendTelemetryAsync();
                await PollProcessorAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error en el ciclo de la carrera");
            }
        }
    }

    // ---------------------------------------------------------------- simulación

    private string Clock()
    {
        var s = (int)(_tick * Dt);
        return $"{s / 60:00}:{s % 60:00}";
    }

    private void Log(string level, string message)
    {
        lock (_gate)
        {
            _logs.Insert(0, new LogEntry(Clock(), level, message));
            if (_logs.Count > 100) _logs.RemoveAt(_logs.Count - 1);
        }
        _logger.Log(level switch { "ERROR" => LogLevel.Error, "WARN" => LogLevel.Warning, _ => LogLevel.Information }, "{Message}", message);
    }

    /// <summary>Avisa una vez al cruzar el umbral y se rearma al bajar (histéresis).</summary>
    private void Watch(HashSet<string> flags, string key, bool active, bool rearm, string level, string message)
    {
        if (active && flags.Add(key)) Log(level, message);
        else if (rearm) flags.Remove(key);
    }

    private double Factor(Driver d)
    {
        var f = 1 + d.Deg / 100 * 0.12 + d.Fuel / 100 * 0.04;
        if (d.Nitro > 0) f *= 0.90;   // el nitro acorta la vuelta y sube la velocidad ~11%
        if (_rain) f *= 1.08;
        if (_scTicks > 0) f *= 1.4;
        return f * d.Skill * (1 + (_rnd.NextDouble() - 0.5) * 0.02);
    }

    private void Step()
    {
        lock (_gate)
        {
            _tick++;
            foreach (var d in _drivers) StepDriver(d);
            if (_scTicks > 0 && --_scTicks == 0) Log("INFO", "Safety car vuelve a boxes: carrera reanudada");
            if (_burstTicks > 0 && --_burstTicks == 0) Log("INFO", "Tráfico normalizado");
        }
    }

    private void StepDriver(Driver d)
    {
        d.LapElapsed += Dt;
        var f = Factor(d);

        if (d.Retired)
        {
            d.Speed = 0;
            d.Temp += (60 - d.Temp) * 0.2;
        }
        else if (d.EngineTicks > 0)
        {
            d.Speed = 0;
            d.Temp += (95 - d.Temp) * 0.2;
            if (--d.EngineTicks == 0) Log("INFO", $"Reparación completada {d.Code}: vuelve a pista");
        }
        else if (d.PitTicks > 0)
        {
            d.Speed = 60;
            d.PitTicks--;
            d.Temp += (90 - d.Temp) * 0.3;
        }
        else
        {
            d.Speed = Math.Max(0, 315 / f + (_rnd.NextDouble() - 0.5) * 8);
            var inc = Dt / (BaseLap * f);
            var boost = d.Nitro > 0;
            d.Deg = Math.Min(100, d.Deg + (0.55 + (_rain ? 0.35 : 0)) * (boost ? 1.25 : 1) * (0.85 + _rnd.NextDouble() * 0.3));
            d.Fuel = Math.Max(0, d.Fuel - 0.2 * d.Burn * (_scTicks > 0 ? 0.5 : 1));

            if (boost)
            {
                d.Battery = Math.Max(0, d.Battery - NitroDrain * (0.9 + _rnd.NextDouble() * 0.2));
                d.Nitro--;
                if (d.Battery <= 0) { d.Nitro = 0; Log("WARN", $"Batería agotada {d.Code}: el nitro se corta"); }
                else if (d.Nitro == 0) Log("INFO", $"Nitro terminado {d.Code}: batería {d.Battery:0}%");
            }
            else
            {
                d.Battery = Math.Min(100, d.Battery + Regen * (0.8 + _rnd.NextDouble() * 0.4));   // recupera energía al frenar
            }

            var target = 90 + d.Speed / 315 * 18 + (boost ? 16 : 0) - (_scTicks > 0 ? 8 : 0) - (_rain ? 4 : 0);
            d.Temp += (target - d.Temp) * 0.25 + (_rnd.NextDouble() - 0.5) * 1.5;

            if (d.Progress + inc >= 1)
            {
                // la vuelta termina dentro de este tick: se interpola el instante exacto
                var frac = (1 - d.Progress) / inc;
                ObserveLap(d, d.LapElapsed - Dt + Dt * frac);
                d.LapElapsed = Dt * (1 - frac);
                d.Progress = d.Progress + inc - 1;
            }
            else
            {
                d.Progress += inc;
            }

            if (d.Fuel <= 0) { d.Retired = true; Log("ERROR", $"Sin combustible {d.Code}: abandona la carrera"); }
        }

        Watch(d.Flags, "tyre70", d.Deg >= 70, d.Deg < 60, "WARN", $"Degradación alta de ruedas {d.Code}: {d.Deg:0}%");
        Watch(d.Flags, "tyre90", d.Deg >= 90, d.Deg < 80, "ERROR", $"Ruedas críticas {d.Code}: {d.Deg:0}%");
        Watch(d.Flags, "hot", d.Temp > 115, d.Temp < 105, "WARN", $"Motor caliente {d.Code}: {d.Temp:0} °C");
        Watch(d.Flags, "crit", d.Temp > 130, d.Temp < 120, "ERROR", $"Motor sobrecalentado {d.Code}: {d.Temp:0} °C");
        Watch(d.Flags, "fuel", d.Fuel < 15 && d.Fuel > 0, d.Fuel > 20, "WARN", $"Combustible bajo {d.Code}: {d.Fuel:0.0} kg");
        Watch(d.Flags, "bat", d.Battery < 15 && !d.Retired, d.Battery > 25, "WARN", $"Batería baja {d.Code}: {d.Battery:0}%, sin nitro disponible");
    }

    private void ObserveLap(Driver d, double seconds)
    {
        d.Laps++;
        Telemetry.LapTime.Record(seconds, Telemetry.Tag("driver", d.Code), Telemetry.Tag("team", d.Team));
        Log("INFO", $"Vuelta {d.Laps} completada {d.Code}: {seconds:0.0} s");
    }

    // ---------------------------------------------------------------- telemetría hacia el procesador

    private async Task SendTelemetryAsync()
    {
        string body;
        lock (_gate)
        {
            var count = _drivers.Count * 6 * (_burstTicks > 0 ? 4 : 1);
            body = BuildBatch(count);
        }
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.CreateClient("processor").PostAsync("/ingest", content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // el procesador puede estar reiniciándose: se detecta en PollProcessorAsync
        }
    }

    /// <summary>Cada evento pesa ~180 bytes, así el tráfico de red recibido por el procesador es real.</summary>
    private string BuildBatch(int count)
    {
        var sb = new StringBuilder(count * 190 + 2);
        var pad = new string('x', 90);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        sb.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var d = _drivers[i % _drivers.Count];
            sb.Append("{\"driver\":\"").Append(d.Code)
              .Append("\",\"kind\":\"").Append(i % 6 == 0 ? "heartbeat" : "sensor")
              .Append("\",\"ts\":").Append(ts)
              .Append(",\"value\":").Append((d.Speed + i).ToString("F3", CultureInfo.InvariantCulture))
              .Append(",\"pad\":\"").Append(pad).Append("\"}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private async Task PollProcessorAsync()
    {
        ProcessorStatus? now = null;
        try
        {
            now = await _http.CreateClient("processor").GetFromJsonAsync<ProcessorStatus>("/status");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }

        lock (_gate)
        {
            var prev = _processor;
            var seen = _processorSeen;
            if (now is null)
            {
                _processor = new ProcessorStatus { Reachable = false };
                if (seen && prev.Reachable) Log("WARN", "El procesador no responde");
                return;
            }

            now.Reachable = true;
            _processor = now;
            _processorSeen = true;
            if (!seen) Log("INFO", "Procesador de telemetría en línea");
            else if (!prev.Reachable) Log("INFO", "El procesador volvió a responder");
            else if (now.UptimeSeconds + 1 < prev.UptimeSeconds)
                Log("ERROR", "El procesador se reinició (OOMKilled): sus contadores vuelven a cero");

            Watch(_sysFlags, "mem", now.MemoryMiB > MemWarnMiB, now.MemoryMiB < MemWarnMiB - 60, "WARN",
                $"Memoria del procesador al 85% del límite: {now.MemoryMiB:0} MiB de 256");
            Watch(_sysFlags, "frozen", now.Frozen, !now.Frozen, "WARN", "Procesador congelado: los eventos se acumulan en cola");
        }
    }

    // ---------------------------------------------------------------- acciones manuales

    public bool Action(string name, string? arg)
    {
        lock (_gate)
        {
            switch (name)
            {
                case "pit": return Pit(arg);
                case "nitro": return Nitro(arg);
                case "safety-car": SafetyCar(); return true;
                case "engine-failure": EngineFailure(); return true;
                case "rain": ToggleRain(); return true;
                case "burst":
                    _burstTicks = 12;
                    Log("WARN", "Pico de tráfico: llegan 4 veces más eventos");
                    return true;
                case "reset": Reset(); return true;
                default: return false;
            }
        }
    }

    private Driver? Find(string? code) => _drivers.FirstOrDefault(d => d.Code == code);

    private bool Pit(string? code)
    {
        var d = Find(code);
        if (d is null) return false;
        if (d.PitTicks > 0 || d.EngineTicks > 0 || d.Retired) return true;
        d.PitTicks = 3; d.Nitro = 0; d.Deg = 0; d.PitStops++;
        d.Flags.Remove("tyre70"); d.Flags.Remove("tyre90");
        Telemetry.PitStops.Add(1, Telemetry.Tag("driver", d.Code), Telemetry.Tag("team", d.Team));
        Log("INFO", $"Parada en boxes {d.Code}: ruedas nuevas");
        return true;
    }

    private bool Nitro(string? code)
    {
        var d = Find(code);
        if (d is null) return false;
        if (d.Nitro > 0) return true;
        if (d.Retired || d.EngineTicks > 0 || d.PitTicks > 0)
            Log("WARN", $"Nitro no disponible {d.Code}: el auto no está en pista");
        else if (d.Battery < NitroMin)
            Log("WARN", $"Nitro rechazado {d.Code}: batería insuficiente ({d.Battery:0}%, mínimo {NitroMin}%)");
        else
        {
            d.Nitro = NitroTicks;
            Log("INFO", $"Nitro activado {d.Code}: batería {d.Battery:0}%");
        }
        return true;
    }

    private void SafetyCar()
    {
        if (_scTicks > 0) return;
        _scTicks = 10;
        Telemetry.Incidents.Add(1, Telemetry.Tag("type", "safety_car"));
        Log("WARN", "Safety car desplegado: velocidad reducida para todos");
    }

    private void EngineFailure()
    {
        var d = Find("HAM");
        if (d is null || d.EngineTicks > 0 || d.Retired) return;
        d.EngineTicks = 6; d.Nitro = 0; d.Temp = 140;
        d.Flags.Add("hot"); d.Flags.Add("crit");
        Telemetry.Incidents.Add(1, Telemetry.Tag("type", "engine"));
        Log("ERROR", "Falla de motor HAM: 140 °C, el auto se detiene");
    }

    private void ToggleRain()
    {
        _rain = !_rain;
        Log(_rain ? "WARN" : "INFO", _rain ? "Comienza a llover: más desgaste y vueltas más lentas" : "Deja de llover: pista seca");
    }

    private void Reset()
    {
        _drivers = NewDrivers();
        _tick = 0; _scTicks = 0; _burstTicks = 0; _rain = false;
        _logs.Clear();
        Log("INFO", "Carrera reiniciada");
    }

    // ---------------------------------------------------------------- lectura para el panel

    public StatusDto GetStatus()
    {
        lock (_gate)
        {
            var drivers = _drivers.Select(d => new DriverDto(
                d.Code, d.Team, d.Color, Math.Round(d.Speed, 1), Math.Round(d.Battery, 1), Math.Round(d.Deg, 1),
                Math.Round(d.Fuel, 1), Math.Round(d.Temp, 1), d.Nitro, d.Laps, d.PitStops, d.Retired,
                d.PitTicks > 0, d.EngineTicks > 0)).ToArray();
            return new StatusDto(Clock(), _rain, _scTicks > 0, _burstTicks > 0, drivers, _processor);
        }
    }

    public LogEntry[] GetLogs()
    {
        lock (_gate) return _logs.ToArray();
    }
}
