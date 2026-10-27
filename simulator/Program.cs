using System.Text.Json;
using F1.Simulator;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// El mismo programa corre como dos contenedores distintos: "emitter" (la carrera) y "processor".
var role = (builder.Configuration["ROLE"] ?? "emitter").ToLowerInvariant();
var otlpEndpoint = builder.Configuration["OTLP_ENDPOINT"] ?? "http://f1-collector:4317";

builder.Services.AddOpenTelemetry()
    // service.instance.id estable: si fuera un GUID al azar, tras un reinicio Prometheus vería una serie nueva
    // en lugar de un counter que vuelve a cero, y resets() no podría detectar el reinicio.
    .ConfigureResource(r => r.AddService(
        serviceName: role == "processor" ? "telemetry-processor" : "race-simulator",
        serviceInstanceId: role == "processor" ? "f1-processor" : "f1-emitter"))
    .WithMetrics(m => m
        .AddMeter(Telemetry.MeterName)
        .AddView("f1_lap_time_seconds",
            new ExplicitBucketHistogramConfiguration { Boundaries = new double[] { 85, 90, 95, 100, 110, 130 } })
        .AddView("f1_telemetry_processing_latency_seconds",
            new ExplicitBucketHistogramConfiguration { Boundaries = new double[] { 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1 } })
        .AddOtlpExporter((exporter, reader) =>
        {
            exporter.Endpoint = new Uri(otlpEndpoint);
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 2000;
        }));

if (role == "processor")
{
    builder.Services.AddSingleton<TelemetryProcessor>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryProcessor>());
}
else
{
    builder.Services.AddHttpClient("processor", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["PROCESSOR_URL"] ?? "http://f1-processor:8080");
        client.Timeout = TimeSpan.FromSeconds(2);
    });
    builder.Services.AddSingleton<RaceEngine>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RaceEngine>());
}

var app = builder.Build();

if (role == "processor")
{
    app.MapPost("/ingest", async (HttpRequest request, TelemetryProcessor processor) =>
    {
        using var doc = await JsonDocument.ParseAsync(request.Body);
        processor.Enqueue(doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 1);
        return Results.Accepted();
    });
    app.MapGet("/status", (TelemetryProcessor processor) => Results.Ok(processor.Status()));
    app.MapPost("/control/freeze", (TelemetryProcessor processor) => { processor.ToggleFreeze(); return Results.Ok(processor.Status()); });
    app.MapPost("/control/slow", (TelemetryProcessor processor) => { processor.Slow(); return Results.Ok(processor.Status()); });
    app.MapPost("/control/leak", (TelemetryProcessor processor) => { processor.ToggleLeak(); return Results.Ok(processor.Status()); });
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapGet("/api/status", (RaceEngine engine) => Results.Ok(engine.GetStatus()));
    app.MapGet("/api/logs", (RaceEngine engine) => Results.Ok(engine.GetLogs()));
    app.MapPost("/api/action/{name}/{arg?}", async (string name, string? arg, RaceEngine engine, IHttpClientFactory http) =>
    {
        // acciones que le pertenecen al procesador: se reenvían por HTTP
        if (name is "freeze" or "slow" or "leak")
        {
            try
            {
                using var response = await http.CreateClient("processor").PostAsync($"/control/{name}", null);
                return response.IsSuccessStatusCode ? Results.Ok() : Results.StatusCode(502);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.StatusCode(502);
            }
        }
        return engine.Action(name, arg) ? Results.Ok() : Results.NotFound();
    });
}

app.Run();
