using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Http.REST;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class QueueSeedingProtectionFixture
    {
        private class ServiceProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> InvokeMethod { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => InvokeMethod(method, args);
        }

        private static T Service<T>(Func<MethodInfo, object[], object> invoke)
            where T : class
        {
            var service = DispatchProxy.Create<T, ServiceProxy>();
            ((ServiceProxy)(object)service).InvokeMethod = invoke;
            return service;
        }

        private readonly List<string> _mutations = new();
        private QueueController _controller;

        [SetUp]
        public void Setup()
        {
            _mutations.Clear();
            var profileRepository = Service<IProfileRepository>((m, a) => new List<QualityProfile>
            {
                new QualityProfile
                {
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                    }
                }
            });
            var profiles = new QualityProfileService(profileRepository, null, null, null, null, null, null);
            var client = Service<IDownloadClient>((m, a) =>
            {
                _mutations.Add(m.Name);
                return null;
            });
            _controller = new QueueController(
                null,
                Service<IQueueService>((m, a) => new NzbDrone.Core.Queue.Queue { DownloadId = a[0].ToString() }),
                Service<IPendingReleaseService>((m, a) =>
                {
                    if (m.Name == nameof(IPendingReleaseService.FindPendingQueueItem))
                    {
                        return (int)a[0] == 3 ? new NzbDrone.Core.Queue.Queue { Id = 3 } : null;
                    }

                    _mutations.Add(m.Name);
                    return null;
                }),
                profiles,
                Service<ITrackedDownloadService>((m, a) =>
                {
                    if (m.Name == nameof(ITrackedDownloadService.Find))
                    {
                        return new TrackedDownload
                        {
                            DownloadItem = new DownloadClientItem { DownloadId = (string)a[0] }
                        };
                    }

                    _mutations.Add(m.Name);
                    return null;
                }),
                Service<IFailedDownloadService>((m, a) => { _mutations.Add(m.Name); return null; }),
                Service<IIgnoredDownloadService>((m, a) => { _mutations.Add(m.Name); return true; }),
                Service<IProvideDownloadClient>((m, a) => client),
                Service<NzbDrone.Core.Blocklisting.IBlocklistService>((m, a) => { _mutations.Add(m.Name); return null; }),
                null,
                Service<IDownloadImportModeResolver>((m, a) => ((DownloadClientItem)a[0]).DownloadId == "1"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void protected_torrent_cannot_be_deleted_even_when_blocklisting(bool blocklist)
        {
            Assert.Throws<BadRequestException>(() => _controller.RemoveAction(1, blocklist: blocklist));
            Assert.That(_mutations, Is.Empty);
        }

        [Test]
        public void bulk_protection_is_checked_before_any_pending_or_client_removals()
        {
            Assert.Throws<BadRequestException>(() => _controller.RemoveMany(
                new QueueBulkResource { Ids = new List<int> { 3, 2, 1 } }, blocklist: true));
            Assert.That(_mutations, Is.Empty);
        }

        [Test]
        public void protected_torrent_can_be_ignored_without_deleting_it()
        {
            _controller.RemoveAction(1, removeFromClient: false);
            Assert.That(_mutations, Is.EqualTo(new[] { "IgnoreDownload", "StopTracking" }));
        }

        [Test]
        public void protected_torrent_can_be_blocklisted_without_deleting_it()
        {
            _controller.RemoveAction(1, removeFromClient: false, blocklist: true);
            Assert.That(_mutations, Is.EqualTo(new[] { "MarkAsFailed", "StopTracking" }));
        }

        [Test]
        public void protected_torrent_can_change_category_without_deleting_it()
        {
            _controller.RemoveAction(1, removeFromClient: false, changeCategory: true);
            Assert.That(_mutations, Is.EqualTo(new[] { "MarkItemAsImported", "StopTracking" }));
        }

        [Test]
        public void unprotected_download_can_still_be_removed()
        {
            _controller.RemoveAction(2, blocklist: true);
            Assert.That(_mutations, Is.EqualTo(new[] { "RemoveItem", "MarkAsFailed", "StopTracking" }));
        }
    }
}
