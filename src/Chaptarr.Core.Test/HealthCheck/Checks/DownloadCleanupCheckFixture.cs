using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NzbDrone.Core.Configuration;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class DownloadCleanupCheckFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void warning_follows_cleanup_pass_and_clears_on_recovery_or_removal(bool removed)
        {
            var downloads = new List<TrackedDownload> { FailedDownload(1) };
            var reads = 0;
            var check = CreateCheck(downloads, () => reads++);
            var runner = new HealthCheckService(new[] { check },
                Proxy<IServerSideNotificationService>((_, _) => new List<NzbDrone.Core.HealthCheck.HealthCheck>()), null,
                Proxy<IEventAggregator>((_, _) => null), new CacheManager(),
                Proxy<IRuntimeInfo>((_, _) => DateTime.UtcNow), LogManager.GetCurrentClassLogger());

            runner.HandleAsync(new DownloadCleanupStatusChangedEvent());
            runner.HandleAsync(new DownloadsProcessedEvent());
            var warning = runner.Results().Single();
            Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(warning.Message, Does.Contain("Dune 1").And.Contain("/downloads/Dune-1").And.Contain("permission denied"));
            Assert.That(reads, Is.EqualTo(1), "One cached inventory read; no disk or download client dependency");
            if (removed)
            {
                var download = downloads.Single();
                downloads.Clear();
                runner.HandleAsync(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { download }));
            }
            else
            {
                downloads[0].CleanupCheckFailed = false;
                runner.HandleAsync(new DownloadCleanupStatusChangedEvent());
            }

            Assert.That(runner.Results(), Is.Empty);
            Assert.That(reads, Is.EqualTo(2));
        }

        [Test]
        public void warning_is_bounded_and_excludes_resolved_or_removed_downloads()
        {
            var downloads = Enumerable.Range(1, 7).Reverse().Select(FailedDownload).ToList();
            var ignored = FailedDownload(8);
            ignored.State = TrackedDownloadState.ImportBlocked;
            downloads.Add(ignored);
            var removed = FailedDownload(9);
            removed.DownloadItem.Removed = true;
            downloads.Add(removed);
            var warning = CreateCheck(downloads, () => { }).Check();
            Assert.That(warning.Message, Does.Contain("Dune 1").And.Contain("Dune 5").And.Contain("2 more"));
            Assert.That(warning.Message, Does.Not.Contain("Dune 6").And.Not.Contain("Dune 7")
                .And.Not.Contain("Dune 8").And.Not.Contain("Dune 9"));
        }

        private static DownloadCleanupCheck CreateCheck(List<TrackedDownload> downloads, Action read)
        {
            return new DownloadCleanupCheck(
                Proxy<ITrackedDownloadService>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ITrackedDownloadService.GetTrackedDownloads)));
                    read();
                    return downloads;
                }),
                new LocalizationService(
                    Proxy<IConfigService>((_, _) => (int)NzbDrone.Core.Languages.Language.English),
                    Proxy<IAppFolderInfo>((_, _) => TestContext.CurrentContext.TestDirectory),
                    new CacheManager(), LogManager.GetCurrentClassLogger()));
        }

        private static TrackedDownload FailedDownload(int id)
        {
            var download = new TrackedDownload
            {
                CleanupCheckFailed = true,
                State = TrackedDownloadState.Imported,
                DownloadItem = new DownloadClientItem
                {
                    Title = "Dune " + id,
                    OutputPath = new OsPath("/downloads/Dune-" + id)
                }
            };
            download.Warn("Unable to read files: permission denied");
            return download;
        }
    }
}
