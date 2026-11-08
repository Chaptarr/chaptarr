using System;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadHistoryServiceDownloadIdCasingFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void get_latest_download_history_item_should_normalize_to_uppercase(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var repository = new DownloadHistoryRepository(database, events);
                var grabbed = repository.Insert(new DownloadHistory
                {
                    DownloadId = "ABC", SourceTitle = "Fixture release",
                    EventType = DownloadHistoryEventType.DownloadGrabbed, Date = DateTime.UtcNow
                });
                var service = new DownloadHistoryService(repository,
                    new HistoryService(new HistoryRepository(database, events), LogManager.GetCurrentClassLogger()));
                Assert.That(service.GetLatestDownloadHistoryItem("abc").Id, Is.EqualTo(grabbed.Id));
                Assert.That(service.GetLatestDownloadHistoryItem(null), Is.Null);
            });
        }
    }
}
