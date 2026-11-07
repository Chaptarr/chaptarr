# Prometheus metrics

ChaptarrNG can expose operational metrics on a separate HTTP listener. Metrics
are disabled by default, and enabling them does not change the interactive API
listener or its authentication settings.

Set these environment variables to enable the listener:

```text
CHAPTARR__METRICS__ENABLED=true
CHAPTARR__METRICS__BINDADDRESS=127.0.0.1
CHAPTARR__METRICS__PORT=9797
```

The default bind address is `127.0.0.1`, and the default port is `9797`. The
listener serves `GET /metrics`; it returns 404 for other paths and does not
serve the interactive API. The port must differ from the Chaptarr HTTP and
HTTPS ports. Invalid or conflicting port settings stop startup with a clear
configuration error.

For Prometheus in the same container or network namespace, a scrape job can be
configured like this:

```yaml
scrape_configs:
  - job_name: chaptarr
    metrics_path: /metrics
    static_configs:
      - targets: ["127.0.0.1:9797"]
```

If Prometheus runs in another container, bind the metrics listener to an
address reachable on the private container network, for example
`CHAPTARR__METRICS__BINDADDRESS=0.0.0.0`, and target that container's network
name and port. Do not publish this port directly to an untrusted network. The
metrics listener does not require an API key and uses plain HTTP; keep it on a
trusted network, restrict it with a firewall, or place it behind a reverse
proxy that provides transport encryption and authentication.

## Metric contract

All metric names begin with `chaptarr_`. Labels use fixed application enums
only. Titles, author names, paths, URLs, provider names and IDs, API keys, task
names, and other user-controlled values are not labels.

| Metric | Type | Meaning |
| --- | --- | --- |
| `chaptarr_health_status{severity}` | Gauge | One-hot current worst health state: `ok`, `notice`, `warning`, `error`, or `unknown`. |
| `chaptarr_health_checks{status}` | Gauge | Active health check counts by fixed result. Non-`ok` counts match the Health API response. |
| `chaptarr_queue_items` | Gauge | Current total queue depth. |
| `chaptarr_queue_items_by_status{status}` | Gauge | Current queue depth by download status: `queued`, `paused`, `downloading`, `completed`, `failed`, `warning`, or `unknown`. |
| `chaptarr_scheduled_task_executions_total{result}` | Counter | Scheduled task executions by `success`, `failure`, `cancelled`, or `unknown`. This is aggregated across task types to keep labels bounded. |
| `chaptarr_scheduled_task_duration_seconds` | Histogram | Scheduled task duration in seconds. |
| `chaptarr_import_list_syncs_total{outcome}` | Counter | Import-list syncs by `success`, `failure`, `cancelled`, or `unknown`. |
| `chaptarr_import_list_decisions_total` | Counter | Book decisions reported by successful import-list syncs. |
| `chaptarr_download_outcomes_total{outcome}` | Counter | Download lifecycle events by `completed`, `failed`, or `ignored`. |
| `chaptarr_import_outcomes_total{outcome}` | Counter | Successfully imported files and incomplete import attempts. |

The endpoint emits UTF-8 Prometheus text exposition format version 0.0.4.
Metric names, label names, and allowed label values are intended to remain
stable; additions are compatible, while renames or semantic changes require
release notes.
