using System;
using System.Collections.Generic;
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
    public class DownloadImportModeResolverFixture
    {
        private sealed class StubDownloadHistoryService : IDownloadHistoryService
        {
            public Func<string, DownloadHistory> LatestGrab { get; set; }

            public bool DownloadAlreadyImported(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestDownloadHistoryItem(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestGrab(string downloadId) => LatestGrab?.Invoke(downloadId);
            public PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec) => throw new NotImplementedException();
            public List<string> RemoveIgnored(int id) => throw new NotImplementedException();
            public List<string> RemoveIgnored(List<int> ids) => throw new NotImplementedException();
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

        private static DownloadClientItem DownloadItem(string downloadId, int downloadClientId, DownloadProtocol protocol, bool canMove)
        {
            return new DownloadClientItem
            {
                Title = "Test Download",
                DownloadId = downloadId,
                CanMoveFiles = canMove,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = downloadClientId,
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

            var logger = LogManager.GetLogger("DownloadImportModeResolverFixture");

            return new DownloadImportModeResolver(downloadHistory, indexerFactory, downloadClientFactory, logger);
        }

        [Test]
        public void should_force_copy_for_grabbed_download_when_indexer_never_move_enabled()
        {
            var history = new StubDownloadHistoryService
            {
                LatestGrab = id => id == "ABC" ? new DownloadHistory { IndexerId = 10 } : null
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
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Copy));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.True);
        }

        [Test]
        public void should_force_copy_for_grabbed_download_when_indexer_never_move_enabled_even_if_move_requested()
        {
            var history = new StubDownloadHistoryService
            {
                LatestGrab = id => id == "ABC" ? new DownloadHistory { IndexerId = 10 } : null
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
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Move, item), Is.EqualTo(ImportMode.Copy));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.True);
        }

        [Test]
        public void should_force_copy_for_unmanaged_download_when_client_flag_enabled()
        {
            var history = new StubDownloadHistoryService { LatestGrab = _ => null };

            var clientDefinition = new DownloadClientDefinition
            {
                Id = 5,
                CopyUnmanagedDownloads = true
            };

            var resolver = BuildResolver(history, _ => null, id => id == 5 ? clientDefinition : null);
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Copy));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.True);
        }

        [Test]
        public void should_return_move_for_unmanaged_download_when_client_flag_disabled()
        {
            var history = new StubDownloadHistoryService { LatestGrab = _ => null };

            var clientDefinition = new DownloadClientDefinition
            {
                Id = 5,
                CopyUnmanagedDownloads = false
            };

            var resolver = BuildResolver(history, _ => null, id => id == 5 ? clientDefinition : null);
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Move));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.False);
        }

        [Test]
        public void should_not_apply_overrides_for_non_torrent_download_clients()
        {
            var history = new StubDownloadHistoryService { LatestGrab = _ => null };

            var clientDefinition = new DownloadClientDefinition
            {
                Id = 5,
                CopyUnmanagedDownloads = true
            };

            var resolver = BuildResolver(history, _ => null, id => id == 5 ? clientDefinition : null);
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Usenet, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Move));
            Assert.That(resolver.ShouldPreserveDownloadClientItem(item), Is.False);
        }

        [Test]
        public void should_use_can_move_files_when_auto()
        {
            var history = new StubDownloadHistoryService { LatestGrab = _ => null };
            var resolver = BuildResolver(history, _ => null, _ => null);
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: false);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Copy));
        }

        [Test]
        public void should_not_override_can_move_files_when_remove_completed_downloads_disabled()
        {
            var history = new StubDownloadHistoryService { LatestGrab = _ => null };

            var clientDefinition = new DownloadClientDefinition
            {
                Id = 5,
                RemoveCompletedDownloads = false
            };

            var resolver = BuildResolver(history, _ => null, id => id == 5 ? clientDefinition : null);
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Move));
        }

        [Test]
        public void should_find_grab_history_with_common_case_variants()
        {
            var history = new StubDownloadHistoryService
            {
                LatestGrab = id => id == "abc" ? new DownloadHistory { IndexerId = 10 } : null
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
            var item = DownloadItem("ABC", downloadClientId: 5, protocol: DownloadProtocol.Torrent, canMove: true);

            Assert.That(resolver.Resolve(ImportMode.Auto, item), Is.EqualTo(ImportMode.Copy));
        }
    }
}
