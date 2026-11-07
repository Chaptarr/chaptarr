using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using HealthCheckModel = NzbDrone.Core.HealthCheck.HealthCheck;
using QueueModel = NzbDrone.Core.Queue.Queue;

namespace NzbDrone.Core.Metrics
{
    public interface IOperationalMetricsService
    {
        string RenderPrometheusText();
    }

    public class PrometheusMetricsService : IOperationalMetricsService,
                                           IHandle<CommandExecutedEvent>,
                                           IHandle<ImportListSyncCompleteEvent>,
                                           IHandle<DownloadCompletedEvent>,
                                           IHandle<DownloadFailedEvent>,
                                           IHandle<DownloadIgnoredEvent>,
                                           IHandle<TrackImportedEvent>,
                                           IHandle<BookImportIncompleteEvent>
    {
        private static readonly string[] TaskResultLabels = { "success", "failure", "cancelled", "unknown" };
        private static readonly string[] ImportListResultLabels = { "success", "failure", "cancelled", "unknown" };
        private static readonly string[] DownloadOutcomeLabels = { "completed", "failed", "ignored" };
        private static readonly string[] ImportOutcomeLabels = { "success", "incomplete" };
        private static readonly string[] QueueStatusLabels = { "queued", "paused", "downloading", "completed", "failed", "warning", "unknown" };
        private static readonly string[] HealthStatusLabels = { "ok", "notice", "warning", "error", "unknown" };
        private static readonly double[] TaskDurationBuckets = { 0.1, 0.5, 1, 5, 10, 30, 60, 300 };

        private readonly Lazy<IQueueService> _queueService;
        private readonly Lazy<IHealthCheckService> _healthCheckService;
        private readonly long[] _scheduledTaskExecutions = new long[TaskResultLabels.Length];
        private readonly long[] _importListSyncs = new long[ImportListResultLabels.Length];
        private readonly long[] _downloadOutcomes = new long[DownloadOutcomeLabels.Length];
        private readonly long[] _importOutcomes = new long[ImportOutcomeLabels.Length];
        private readonly Histogram _scheduledTaskDuration = new Histogram(TaskDurationBuckets);
        private long _importListDecisions;

        public PrometheusMetricsService(Lazy<IQueueService> queueService, Lazy<IHealthCheckService> healthCheckService)
        {
            _queueService = queueService;
            _healthCheckService = healthCheckService;
        }

        public void Handle(CommandExecutedEvent message)
        {
            var command = message?.Command;
            if (command?.Body == null)
            {
                return;
            }

            var outcome = GetCommandOutcome(command);

            if (command.Trigger == CommandTrigger.Scheduled)
            {
                Interlocked.Increment(ref _scheduledTaskExecutions[outcome]);
                _scheduledTaskDuration.Observe(command.Duration?.TotalSeconds ?? 0);
            }

            if (command.Body is ImportListSyncCommand || command.Body is HardcoverLibrarySyncCommand)
            {
                // Successful runs are recorded by ImportListSyncCompleteEvent. This event is
                // used only for failed, cancelled, or otherwise unfinished attempts.
                if (outcome != 0)
                {
                    Interlocked.Increment(ref _importListSyncs[outcome]);
                }
            }
        }

        public void Handle(ImportListSyncCompleteEvent message)
        {
            Interlocked.Increment(ref _importListSyncs[0]);
            Interlocked.Add(ref _importListDecisions, message?.ProcessedDecisions?.Count ?? 0);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            Interlocked.Increment(ref _downloadOutcomes[0]);
        }

        public void Handle(DownloadFailedEvent message)
        {
            Interlocked.Increment(ref _downloadOutcomes[1]);
        }

        public void Handle(DownloadIgnoredEvent message)
        {
            Interlocked.Increment(ref _downloadOutcomes[2]);
        }

        public void Handle(TrackImportedEvent message)
        {
            Interlocked.Increment(ref _importOutcomes[0]);
        }

        public void Handle(BookImportIncompleteEvent message)
        {
            Interlocked.Increment(ref _importOutcomes[1]);
        }

        public string RenderPrometheusText()
        {
            var builder = new StringBuilder();
            var queueItems = _queueService.Value.GetQueue();
            var healthChecks = _healthCheckService.Value.Results();

            WriteCounterFamily(builder,
                "chaptarr_scheduled_task_executions_total",
                "Completed scheduled task executions, grouped by result.",
                "result",
                TaskResultLabels,
                _scheduledTaskExecutions);

            WriteHistogram(builder,
                "chaptarr_scheduled_task_duration_seconds",
                "Scheduled task execution duration in seconds.",
                _scheduledTaskDuration.Snapshot());

            WriteCounterFamily(builder,
                "chaptarr_import_list_syncs_total",
                "Import-list sync attempts, grouped by result.",
                "outcome",
                ImportListResultLabels,
                _importListSyncs);

            WriteCounter(builder,
                "chaptarr_import_list_decisions_total",
                "Book decisions processed by successful import-list syncs.",
                Interlocked.Read(ref _importListDecisions));

            WriteCounterFamily(builder,
                "chaptarr_download_outcomes_total",
                "Download lifecycle outcomes.",
                "outcome",
                DownloadOutcomeLabels,
                _downloadOutcomes);

            WriteCounterFamily(builder,
                "chaptarr_import_outcomes_total",
                "Import lifecycle outcomes, counting successfully imported files and incomplete attempts.",
                "outcome",
                ImportOutcomeLabels,
                _importOutcomes);

            WriteQueueGauges(builder, queueItems);
            WriteHealthGauge(builder, healthChecks);

            return builder.ToString();
        }

        private static int GetCommandOutcome(CommandModel command)
        {
            if (command.Status == CommandStatus.Cancelled)
            {
                return 2;
            }

            if (command.Status == CommandStatus.Failed || command.Result == CommandResult.Unsuccessful)
            {
                return 1;
            }

            if (command.Status == CommandStatus.Completed)
            {
                return 0;
            }

            return 3;
        }

        private static void WriteQueueGauges(StringBuilder builder, List<QueueModel> queueItems)
        {
            var counts = new long[QueueStatusLabels.Length];
            var queue = queueItems ?? new List<QueueModel>();

            foreach (var item in queue)
            {
                if (Enum.TryParse(item?.Status, true, out DownloadItemStatus status) &&
                    Enum.IsDefined(typeof(DownloadItemStatus), status))
                {
                    counts[(int)status]++;
                }
                else
                {
                    counts[QueueStatusLabels.Length - 1]++;
                }
            }

            WriteGauge(builder, "chaptarr_queue_items", "Current queue depth.", queue.Count);
            WriteGaugeFamily(builder,
                "chaptarr_queue_items_by_status",
                "Current queue depth grouped by download status.",
                "status",
                QueueStatusLabels,
                counts);
        }

        private static void WriteHealthGauge(StringBuilder builder, List<HealthCheckModel> healthChecks)
        {
            var status = HealthCheckResult.Ok;
            var counts = new long[HealthStatusLabels.Length];
            var checks = healthChecks ?? new List<HealthCheckModel>();

            if (checks.Any())
            {
                status = checks.Max(check => check?.Type ?? HealthCheckResult.Ok);
            }

            foreach (var check in checks)
            {
                var checkStatus = check?.Type ?? HealthCheckResult.Ok;
                var checkIndex = Enum.IsDefined(typeof(HealthCheckResult), checkStatus) ? (int)checkStatus : HealthStatusLabels.Length - 1;
                counts[checkIndex]++;
            }

            WriteGaugeFamily(builder,
                "chaptarr_health_checks",
                "Current health checks grouped by result; non-ok values match the health API response.",
                "status",
                HealthStatusLabels,
                counts);

            var index = Enum.IsDefined(typeof(HealthCheckResult), status) ? (int)status : HealthStatusLabels.Length - 1;
            WriteGaugeFamily(builder,
                "chaptarr_health_status",
                "One-hot gauge for the most severe current health-check result.",
                "severity",
                HealthStatusLabels,
                Enumerable.Range(0, HealthStatusLabels.Length).Select(value => value == index ? 1L : 0L).ToArray());
        }

        private static void WriteCounterFamily(StringBuilder builder, string name, string help, string labelName, string[] labels, long[] values)
        {
            AppendFamilyHeader(builder, name, help, "counter");
            for (var i = 0; i < labels.Length; i++)
            {
                AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}{{{1}=\"{2}\"}} {3}", name, labelName, labels[i], Interlocked.Read(ref values[i])));
            }
        }

        private static void WriteGaugeFamily(StringBuilder builder, string name, string help, string labelName, string[] labels, long[] values)
        {
            AppendFamilyHeader(builder, name, help, "gauge");
            for (var i = 0; i < labels.Length; i++)
            {
                AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}{{{1}=\"{2}\"}} {3}", name, labelName, labels[i], values[i]));
            }
        }

        private static void WriteCounter(StringBuilder builder, string name, string help, long value)
        {
            AppendFamilyHeader(builder, name, help, "counter");
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0} {1}", name, value));
        }

        private static void WriteGauge(StringBuilder builder, string name, string help, long value)
        {
            AppendFamilyHeader(builder, name, help, "gauge");
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0} {1}", name, value));
        }

        private static void WriteHistogram(StringBuilder builder, string name, string help, HistogramSnapshot snapshot)
        {
            AppendFamilyHeader(builder, name, help, "histogram");
            for (var i = 0; i < snapshot.Bounds.Length; i++)
            {
                AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}_bucket{{le=\"{1}\"}} {2}", name, Format(snapshot.Bounds[i]), snapshot.Buckets[i]));
            }

            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}_bucket{{le=\"+Inf\"}} {1}", name, snapshot.Count));
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}_sum {1}", name, Format(snapshot.Sum)));
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "{0}_count {1}", name, snapshot.Count));
        }

        private static void AppendFamilyHeader(StringBuilder builder, string name, string help, string type)
        {
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "# HELP {0} {1}", name, help));
            AppendLine(builder, string.Format(CultureInfo.InvariantCulture, "# TYPE {0} {1}", name, type));
        }

        private static void AppendLine(StringBuilder builder, string line)
        {
            builder.Append(line).Append('\n');
        }

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private sealed class Histogram
        {
            private readonly object _gate = new object();
            private readonly double[] _bounds;
            private readonly long[] _buckets;
            private long _count;
            private double _sum;

            public Histogram(double[] bounds)
            {
                _bounds = bounds;
                _buckets = new long[bounds.Length];
            }

            public void Observe(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                {
                    return;
                }

                lock (_gate)
                {
                    _count++;
                    _sum += value;

                    for (var i = 0; i < _bounds.Length; i++)
                    {
                        if (value <= _bounds[i])
                        {
                            _buckets[i]++;
                        }
                    }
                }
            }

            public HistogramSnapshot Snapshot()
            {
                lock (_gate)
                {
                    return new HistogramSnapshot(_bounds, (long[])_buckets.Clone(), _count, _sum);
                }
            }
        }

        private sealed class HistogramSnapshot
        {
            public double[] Bounds { get; }
            public long[] Buckets { get; }
            public long Count { get; }
            public double Sum { get; }

            public HistogramSnapshot(double[] bounds, long[] buckets, long count, double sum)
            {
                Bounds = bounds;
                Buckets = buckets;
                Count = count;
                Sum = sum;
            }
        }
    }
}
