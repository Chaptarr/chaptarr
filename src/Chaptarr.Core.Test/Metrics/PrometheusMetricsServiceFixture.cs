using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Metrics;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Metrics
{
    [TestFixture]
    public class PrometheusMetricsServiceFixture
    {
        private class QueueServiceProxy : DispatchProxy
        {
            public List<NzbDrone.Core.Queue.Queue> Items { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IQueueService.GetQueue) => Items,
                    nameof(IQueueService.Find) => null,
                    nameof(IQueueService.Remove) => null,
                    _ => throw new NotImplementedException($"Test proxy does not implement IQueueService.{targetMethod?.Name}")
                };
            }
        }

        private class HealthCheckServiceProxy : DispatchProxy
        {
            public List<NzbDrone.Core.HealthCheck.HealthCheck> Items { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHealthCheckService.Results))
                {
                    return Items;
                }

                throw new NotImplementedException($"Test proxy does not implement IHealthCheckService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_export_scheduled_task_and_import_list_results()
        {
            var subject = CreateSubject(out _, out _);

            subject.Handle(new CommandExecutedEvent(new CommandModel
            {
                Body = new ImportListSyncCommand(),
                Trigger = CommandTrigger.Scheduled,
                Status = CommandStatus.Completed,
                Result = CommandResult.Successful,
                Duration = TimeSpan.FromSeconds(4.2)
            }));
            subject.Handle(new ImportListSyncCompleteEvent(new List<NzbDrone.Core.Books.Book>
            {
                new() { Title = "PRIVATE BOOK TITLE" }
            }));
            subject.Handle(new CommandExecutedEvent(new CommandModel
            {
                Body = new ImportListSyncCommand(),
                Trigger = CommandTrigger.Scheduled,
                Status = CommandStatus.Failed,
                Result = CommandResult.Unsuccessful,
                Duration = TimeSpan.FromSeconds(2)
            }));

            var metrics = subject.RenderPrometheusText();

            Assert.That(metrics, Does.Contain("chaptarr_scheduled_task_executions_total{result=\"success\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_scheduled_task_executions_total{result=\"failure\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_scheduled_task_duration_seconds_bucket{le=\"5\"} 2"));
            Assert.That(metrics, Does.Contain("chaptarr_scheduled_task_duration_seconds_count 2"));
            Assert.That(metrics, Does.Contain("chaptarr_import_list_syncs_total{outcome=\"success\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_import_list_syncs_total{outcome=\"failure\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_import_list_decisions_total 1"));
            Assert.That(metrics, Does.Not.Contain("PRIVATE BOOK TITLE"));
            Assert.That(metrics, Does.EndWith("\n"));
        }

        [Test]
        public void should_limit_labels_to_fixed_values_and_match_queue_and_health_snapshots()
        {
            var subject = CreateSubject(out var queueProxy, out var healthProxy);
            queueProxy.Items = new List<NzbDrone.Core.Queue.Queue>
            {
                new() { Status = "Warning" },
                new() { Status = "PRIVATE QUEUE STATUS" }
            };
            healthProxy.Items = new List<NzbDrone.Core.HealthCheck.HealthCheck>
            {
                new(typeof(PrometheusMetricsService), HealthCheckResult.Warning, "PRIVATE HEALTH MESSAGE")
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "PRIVATE_DOWNLOAD_ID",
                    Title = "PRIVATE DOWNLOAD TITLE",
                    OutputPath = new NzbDrone.Common.Disk.OsPath("/private/download/path"),
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "PRIVATE PROVIDER NAME",
                        Type = "PRIVATE PROVIDER TYPE"
                    }
                }
            };

            subject.Handle(new DownloadCompletedEvent(trackedDownload, 0));
            subject.Handle(new DownloadFailedEvent { SourceTitle = "PRIVATE FAILED TITLE", DownloadId = "PRIVATE_FAILED_ID", Message = "PRIVATE FAILURE" });
            subject.Handle(new DownloadIgnoredEvent { SourceTitle = "PRIVATE IGNORED TITLE", DownloadId = "PRIVATE_IGNORED_ID", Message = "PRIVATE IGNORE" });
            subject.Handle(new TrackImportedEvent(null, null, null, true, trackedDownload.DownloadItem));
            subject.Handle(new BookImportIncompleteEvent(trackedDownload));

            var metrics = subject.RenderPrometheusText();

            Assert.That(metrics, Does.Contain("chaptarr_queue_items 2"));
            Assert.That(metrics, Does.Contain("chaptarr_queue_items_by_status{status=\"warning\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_queue_items_by_status{status=\"unknown\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_health_checks{status=\"warning\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_health_status{severity=\"warning\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_download_outcomes_total{outcome=\"completed\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_download_outcomes_total{outcome=\"failed\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_download_outcomes_total{outcome=\"ignored\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_import_outcomes_total{outcome=\"success\"} 1"));
            Assert.That(metrics, Does.Contain("chaptarr_import_outcomes_total{outcome=\"incomplete\"} 1"));
            Assert.That(metrics, Does.Not.Contain("PRIVATE"));
            Assert.That(metrics, Does.Not.Contain("/private/"));
        }

        private static PrometheusMetricsService CreateSubject(out QueueServiceProxy queueProxy, out HealthCheckServiceProxy healthProxy)
        {
            var queueService = DispatchProxy.Create<IQueueService, QueueServiceProxy>();
            queueProxy = (QueueServiceProxy)(object)queueService;
            var healthCheckService = DispatchProxy.Create<IHealthCheckService, HealthCheckServiceProxy>();
            healthProxy = (HealthCheckServiceProxy)(object)healthCheckService;

            return new PrometheusMetricsService(new Lazy<IQueueService>(() => queueService), new Lazy<IHealthCheckService>(() => healthCheckService));
        }
    }
}
