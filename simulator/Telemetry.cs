using System.Diagnostics.Metrics;

namespace F1.Simulator;

/// <summary>
/// Todas las métricas de la demo. Los nombres coinciden con los que ve Prometheus.
/// </summary>
public static class Telemetry
{
    public const string MeterName = "F1.Telemetry";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // Negocio (los gauges de velocidad, ruedas, combustible, motor y batería se crean en RaceEngine)
    public static readonly Histogram<double> LapTime =
        Meter.CreateHistogram<double>("f1_lap_time_seconds", description: "Tiempo de vuelta");
    public static readonly Counter<long> PitStops =
        Meter.CreateCounter<long>("f1_pit_stops_total", description: "Paradas en boxes");
    public static readonly Counter<long> Incidents =
        Meter.CreateCounter<long>("f1_incidents_total", description: "Incidentes de carrera");

    // Aplicación (procesador de telemetría)
    public static readonly Histogram<double> ProcessingLatency =
        Meter.CreateHistogram<double>("f1_telemetry_processing_latency_seconds", description: "Latencia de procesamiento de telemetría");
    public static readonly Counter<long> EventsProcessed =
        Meter.CreateCounter<long>("f1_telemetry_events_processed_total", description: "Eventos de telemetría procesados (heartbeat)");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
