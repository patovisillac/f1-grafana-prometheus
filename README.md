# Demo de observabilidad: telemetría de una carrera de F1

Un simulador de F1 emite métricas por OTLP; un Collector las recibe; Prometheus las recolecta; Grafana las dibuja.
Stack independiente del de PharmaGo (otros nombres de contenedor y otros puertos).

```
Panel de control ──► f1-emitter (carrera) ──┐
                                            ├─ OTLP ─► f1-collector ─► f1-prometheus ─► f1-grafana
        f1-processor (procesador, 256 MiB) ─┘                ▲
                      └─ memoria y red del contenedor ──► f1-cadvisor
```

## Cómo usarlo

Requisito: Docker Desktop abierto. Mantené **apagados** los contenedores de PharmaGo (SQL Server consume mucha memoria).

```bash
docker compose up --build -d      # primera vez: baja imágenes y compila
docker compose down -v            # para apagarlo y borrar los datos (arranque limpio la próxima vez)
```

Usá siempre `down -v` antes de volver a levantarlo. Prometheus guarda sus datos en un volumen anónimo que
`up --force-recreate` conserva, y cAdvisor y el Collector retienen unos minutos las series de los contenedores
anteriores: sin eso, los gráficos muestran líneas repetidas hasta que esas series salen de la ventana.

| Qué | URL |
|---|---|
| Panel de control (botones de la demo) | http://localhost:5100 |
| Grafana (dashboard ya cargado, sin login) | http://localhost:3001 |
| Prometheus (para mostrar PromQL) | http://localhost:9091 |
| Alertas en Prometheus | http://localhost:9091/alerts |
| Métricas crudas del Collector (formato Prometheus) | http://localhost:8890/metrics |
| cAdvisor | http://localhost:8081 |

## Qué métricas hay

| Nivel | Métrica | Tipo |
|---|---|---|
| Negocio | `f1_lap_time_seconds` | histogram |
| | `f1_tyre_degradation_percent`, `f1_fuel_remaining_kg`, `f1_engine_temperature_celsius`, `f1_battery_charge_percent`, `f1_speed_kmh` | gauge |
| | `f1_pit_stops_total`, `f1_incidents_total` | counter |
| Aplicación | `f1_telemetry_processing_latency_seconds` | histogram |
| | `f1_telemetry_events_processed_total` (heartbeat) | counter |
| Infraestructura | `container_memory_usage_bytes`, `container_spec_memory_limit_bytes` | gauge |
| | `container_network_receive_bytes_total`, `container_oom_events_total` | counter |

Las de infraestructura las publica cAdvisor y Prometheus las lee directo; el resto viaja por OTLP.

## Guion sugerido para la demo

1. Mostrar el dashboard y la pestaña de métricas crudas del Collector (el formato).
2. **Nitro** en un piloto: se ve en velocidad, batería, temperatura y tiempo de vuelta.
3. **Congelar procesador** + **Pico de tráfico**: el heartbeat cae a 0, la memoria sube hasta los 256 MiB,
   Docker mata el contenedor (OOMKilled) y se reinicia solo. El counter vuelve a cero y `resets()` lo detecta.
4. **Alerta de ruedas:** con **Nitro** el desgaste sube más rápido. Al pasar el 75 % durante 10 s, la alerta
   `DesgasteDeRuedasAlto` (definida en `prometheus/alerts.yml`) pasa de *pending* a *firing*: se ve en
   http://localhost:9091/alerts, en Grafana (Alerting → Alert rules) y en el panel "Alertas de ruedas activas".
   Una **parada en boxes** baja el desgaste y la alerta se resuelve.

## Alertas

`alerta-sonora.html` (se abre con doble clic) consulta a Prometheus cada 2 s y suena cuando una alerta pasa a
*firing* (y con otro tono cuando se resuelve). Hay que apretar "Activar sonido" una vez al abrirla.

Prometheus evalúa la regla cada 5 s. Estados: *inactive* (todo bien), *pending* (la condición se cumple pero aún no
pasaron los 10 s del `for`) y *firing* (disparada). La demo **no incluye Alertmanager**: la alerta se ve en las
interfaces, pero no envía correos ni mensajes.

## Qué es real y qué es simulado

- Real: el tráfico de red hacia el procesador, la memoria (usa memoria nativa de verdad), el límite de 256 MiB,
  el OOM de Docker y el reinicio automático.
- Simulado: la carrera (valores generados con reglas simples) y la latencia de procesamiento.
- No incluye logs ni trazas: este stack guarda únicamente métricas.
