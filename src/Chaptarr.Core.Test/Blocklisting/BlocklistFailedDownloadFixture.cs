using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistFailedDownloadFixture
    {
        private const int AuthorId = 17;
        private const string SourceTitle = "Isaac Asimov - Den nakna solen (The naked sun)";
        private const string TorrentHash = "5CE1F006D42D7A2A7C7164A2293F9F8CA9FF15CA";

        private BlocklistRepositoryProxy _repositoryProxy;
        private BlocklistService _subject;

        [SetUp]
        public void SetUp()
        {
            var repository = DispatchProxy.Create<IBlocklistRepository, BlocklistRepositoryProxy>();
            _repositoryProxy = (BlocklistRepositoryProxy)(object)repository;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = new Author
            {
                Id = AuthorId,
                HardcoverAuthorId = "hc:9689"
            };

            _subject = new BlocklistService(
                repository,
                authorService,
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_block_a_failed_torrent_by_the_live_download_client_hash_when_grab_data_has_no_hash()
        {
            var failed = BuildFailedEvent();
            failed.TrackedDownload = new TrackedDownload
            {
                Protocol = DownloadProtocol.Torrent,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = TorrentHash,
                    Title = SourceTitle
                }
            };

            _subject.Handle(failed);

            Assert.That(_repositoryProxy.Inserted.TorrentInfoHash, Is.EqualTo(TorrentHash));
            Assert.That(_subject.Blocklisted(AuthorId, BuildTorrentRelease(TorrentHash)), Is.True,
                "the next RSS/search result with the same info hash must be rejected");
        }

        [Test]
        public void should_keep_the_grab_history_hash_fallback_when_no_tracked_download_exists()
        {
            var failed = BuildFailedEvent();
            failed.Data["torrentInfoHash"] = TorrentHash;

            _subject.Handle(failed);

            Assert.That(_repositoryProxy.Inserted.TorrentInfoHash, Is.EqualTo(TorrentHash));
            Assert.That(_subject.Blocklisted(AuthorId, BuildTorrentRelease(TorrentHash)), Is.True);
        }

        [Test]
        public void should_block_a_failed_usenet_release_when_the_indexer_changes_its_published_timestamp()
        {
            _repositoryProxy.Inserted = new Blocklist
            {
                AuthorProviderIds = new List<string> { "hc:9689" },
                SourceTitle = SourceTitle,
                Protocol = DownloadProtocol.Usenet,
                Indexer = "NZBgeek (Prowlarr)",
                PublishedDate = new DateTime(2022, 4, 30, 10, 26, 30),
                Size = 361219000
            };

            var repeatedRelease = new ReleaseInfo
            {
                Title = SourceTitle,
                DownloadProtocol = DownloadProtocol.Usenet,
                Indexer = "NZBgeek (Prowlarr)",
                PublishDate = new DateTime(2022, 3, 31, 10, 18, 00),
                Size = 361219000
            };

            Assert.That(_subject.Blocklisted(AuthorId, repeatedRelease), Is.True,
                "the same named NZB must not be retried merely because its timestamp changed");
        }

        private static DownloadFailedEvent BuildFailedEvent()
        {
            return new DownloadFailedEvent
            {
                AuthorId = AuthorId,
                BookIds = new List<int>(),
                SourceTitle = SourceTitle,
                Message = "Download failed",
                Data = new Dictionary<string, string>
                {
                    ["protocol"] = ((int)DownloadProtocol.Torrent).ToString(),
                    ["indexer"] = "The Pirate Bay (Prowlarr)",
                    ["publishedDate"] = "2026-08-06T19:12:42Z",
                    ["size"] = "123456789",
                    ["indexerFlags"] = "0"
                }
            };
        }

        private static TorrentInfo BuildTorrentRelease(string hash)
        {
            return new TorrentInfo
            {
                Title = SourceTitle,
                Indexer = "The Pirate Bay (Prowlarr)",
                DownloadProtocol = DownloadProtocol.Torrent,
                InfoHash = hash
            };
        }

        private class BlocklistRepositoryProxy : DispatchProxy
        {
            public Blocklist Inserted { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBlocklistRepository.Insert))
                {
                    Inserted = (Blocklist)args[0];
                    Inserted.Id = 1;
                    return Inserted;
                }

                if (targetMethod?.Name == nameof(IBlocklistRepository.BlocklistedByTorrentInfoHash))
                {
                    var hash = (string)args[1];
                    return Inserted == null || Inserted.AuthorProviderIds.Count == 0 ||
                           Inserted.TorrentInfoHash?.IndexOf(hash, StringComparison.InvariantCultureIgnoreCase) < 0
                        ? new List<Blocklist>()
                        : new List<Blocklist> { Inserted };
                }

                if (targetMethod?.Name == nameof(IBlocklistRepository.BlocklistedByTitle))
                {
                    var sourceTitle = (string)args[1];
                    return Inserted == null || Inserted.AuthorProviderIds.Count == 0 ||
                           Inserted.SourceTitle?.IndexOf(sourceTitle, StringComparison.InvariantCultureIgnoreCase) < 0
                        ? new List<Blocklist>()
                        : new List<Blocklist> { Inserted };
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author?.Id == (int)args[0] ? Author : null;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }
    }
}
