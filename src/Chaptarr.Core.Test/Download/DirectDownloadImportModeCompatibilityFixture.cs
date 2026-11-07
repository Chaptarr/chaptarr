using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadImportModeCompatibilityFixture
    {
        private sealed class StubDownloadHistoryService : IDownloadHistoryService
        {
            public Func<string, DownloadHistory> LatestGrab { get; set; }

            public bool DownloadAlreadyImported(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestDownloadHistoryItem(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestGrab(string downloadId) => LatestGrab?.Invoke(downloadId);
            public PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec) => throw new NotImplementedException();
            public System.Collections.Generic.List<string> RemoveIgnored(int id) => throw new NotImplementedException();
            public System.Collections.Generic.List<string> RemoveIgnored(System.Collections.Generic.List<int> ids) => throw new NotImplementedException();
        }

        private sealed class TestTorrentIndexerSettings : ITorrentIndexerSettings
        {
            public string BaseUrl { get; set; }
            public int? EarlyReleaseLimit { get; set; }
            public int MinimumSeeders { get; set; }
            public SeedCriteriaSettings SeedCriteria { get; set; } = new();
            public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }
            public NzbDroneValidationResult Validate() => new();
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public Func<int, IndexerDefinition> Find { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(NzbDrone.Core.ThingiProvider.IProviderFactory<IIndexer, IndexerDefinition>.Find) &&
                    args?.Length == 1 &&
                    args[0] is int id)
                {
                    return Find?.Invoke(id);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IIndexerFactory).Name}.{targetMethod?.Name}");
            }
        }

        private class DownloadClientFactoryProxy : DispatchProxy
        {
            public Func<int, DownloadClientDefinition> Find { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(NzbDrone.Core.ThingiProvider.IProviderFactory<IDownloadClient, DownloadClientDefinition>.Find) &&
                    args?.Length == 1 &&
                    args[0] is int id)
                {
                    return Find?.Invoke(id);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IDownloadClientFactory).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_not_apply_torrent_seeding_preservation_rules_to_direct_downloads()
        {
            var history = new StubDownloadHistoryService
            {
                LatestGrab = id => id == "DIRECT-1" ? new DownloadHistory { IndexerId = 10 } : null
            };

            var indexerDefinition = new IndexerDefinition { Id = 10 };
            indexerDefinition.Settings = new TestTorrentIndexerSettings
            {
                SeedCriteria = new SeedCriteriaSettings
                {
                    NeverMoveOnImport = true
                }
            };

            var resolver = BuildResolver(
                history,
                id => id == 10 ? indexerDefinition : null,
                id => id == 5 ? new DownloadClientDefinition { Id = 5, CopyUnmanagedDownloads = true } : null);

            var item = BuildDownloadItem("DIRECT-1", DownloadProtocol.Direct, canMoveFiles: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Move));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.False);
        }

        [Test]
        public void should_keep_existing_torrent_preservation_rules_unchanged()
        {
            var history = new StubDownloadHistoryService
            {
                LatestGrab = id => id == "TORRENT-1" ? new DownloadHistory { IndexerId = 10 } : null
            };

            var indexerDefinition = new IndexerDefinition { Id = 10 };
            indexerDefinition.Settings = new TestTorrentIndexerSettings
            {
                SeedCriteria = new SeedCriteriaSettings
                {
                    NeverMoveOnImport = true
                }
            };

            var resolver = BuildResolver(history, id => id == 10 ? indexerDefinition : null, _ => null);
            var item = BuildDownloadItem("TORRENT-1", DownloadProtocol.Torrent, canMoveFiles: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Copy));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.True);
        }

        private static DownloadClientItem BuildDownloadItem(string downloadId, DownloadProtocol protocol, bool canMoveFiles)
        {
            return new DownloadClientItem
            {
                Title = "Test Download",
                DownloadId = downloadId,
                CanMoveFiles = canMoveFiles,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 5,
                    Name = "Test Client",
                    Protocol = protocol
                }
            };
        }

        private static DownloadImportModeResolver BuildResolver(
            StubDownloadHistoryService downloadHistory,
            Func<int, IndexerDefinition> findIndexer,
            Func<int, DownloadClientDefinition> findClient)
        {
            var indexerFactory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            ((IndexerFactoryProxy)(object)indexerFactory).Find = findIndexer;

            var downloadClientFactory = DispatchProxy.Create<IDownloadClientFactory, DownloadClientFactoryProxy>();
            ((DownloadClientFactoryProxy)(object)downloadClientFactory).Find = findClient;

            return new DownloadImportModeResolver(downloadHistory, indexerFactory, downloadClientFactory, LogManager.GetLogger("DirectDownloadImportModeCompatibilityFixture"));
        }
    }
}
