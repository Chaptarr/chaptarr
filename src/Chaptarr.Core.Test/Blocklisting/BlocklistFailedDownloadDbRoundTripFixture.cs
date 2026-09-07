using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistFailedDownloadDbRoundTripFixture
    {
        private const int AuthorId = 17;
        private const string SourceTitle = "Isaac Asimov - Den nakna solen (The naked sun)";
        private const string Indexer = "The Pirate Bay (Prowlarr)";
        private const string TorrentHash = "5CE1F006D42D7A2A7C7164A2293F9F8CA9FF15CA";

        private string _databasePath;
        private BlocklistService _subject;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"blocklist_roundtrip_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                connection.Execute(BuildCreateTableSql<Author>("Authors"));
                connection.Execute(BuildCreateTableSql<Blocklist>("Blocklist"));
                connection.Execute(
                    @"INSERT INTO ""Authors"" (""Id"", ""GoodreadsAuthorId"", ""HardcoverAuthorId"") VALUES (@Id, @Gr, @Hc);",
                    new { Id = AuthorId, Gr = "gr:999015", Hc = "hc:9689" });
            }

            var database = new Database("main", () =>
            {
                var conn = new SqliteConnection(connectionString);
                conn.Open();
                return conn;
            });

            var repository = new BlocklistRepository(new MainDatabase(database), new StubEventAggregator());

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = new Author
            {
                Id = AuthorId,
                GoodreadsAuthorId = "gr:999015",
                HardcoverAuthorId = "hc:9689"
            };

            _subject = new BlocklistService(
                repository,
                authorService,
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }
            }
            catch
            {
            }
        }

        [Test]
        public void failed_torrent_should_be_rejected_through_the_real_repository_round_trip()
        {
            _subject.Handle(BuildFailedEvent());

            // Hash-bearing candidate with the same hash: rejected via the real author-token
            // intersect (service-side canonicalized tokens vs repository-side raw columns)
            // and the real TorrentInfoHash filter.
            Assert.That(_subject.Blocklisted(AuthorId, BuildTorrentRelease(TorrentHash)), Is.True,
                "the failed release must be blocklisted for a hash-bearing candidate after a real DB round trip");

            // Negative control: a different hash must not match, proving the filter discriminates.
            Assert.That(_subject.Blocklisted(AuthorId, BuildTorrentRelease("0000000000000000000000000000000000000000")), Is.False,
                "a different info hash must not be blocklisted");

            // Hashless candidate: matched through the title+indexer fallback path.
            Assert.That(_subject.Blocklisted(AuthorId, BuildTorrentRelease(null)), Is.True,
                "a hashless candidate with the same title and indexer must be blocklisted");
        }

        [Test]
        public void failed_direct_release_should_match_only_direct_candidates_after_a_real_repository_round_trip()
        {
            _subject.Handle(BuildFailedEvent(DownloadProtocol.Direct));

            Assert.Multiple(() =>
            {
                Assert.That(_subject.Blocklisted(AuthorId, BuildDirectRelease()), Is.True,
                    "the failed direct release must be blocklisted for a matching direct candidate");
                Assert.That(_subject.Blocklisted(AuthorId, BuildUsenetRelease()), Is.False,
                    "a direct blocklist entry must not be treated as a usenet blocklist entry");
            });
        }

        private static DownloadFailedEvent BuildFailedEvent(DownloadProtocol protocol = DownloadProtocol.Torrent)
        {
            return new DownloadFailedEvent
            {
                AuthorId = AuthorId,
                BookIds = new List<int>(),
                SourceTitle = SourceTitle,
                Message = "Download failed",
                Data = new Dictionary<string, string>
                {
                    ["protocol"] = ((int)protocol).ToString(),
                    ["indexer"] = Indexer,
                    ["publishedDate"] = "2026-08-06T19:12:42Z",
                    ["size"] = "123456789",
                    ["indexerFlags"] = "0"
                },
                TrackedDownload = new TrackedDownload
                {
                    Protocol = protocol,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = TorrentHash,
                        Title = SourceTitle
                    }
                }
            };
        }

        private static TorrentInfo BuildTorrentRelease(string hash)
        {
            return new TorrentInfo
            {
                Title = SourceTitle,
                Indexer = Indexer,
                DownloadProtocol = DownloadProtocol.Torrent,
                InfoHash = hash
            };
        }

        private static ReleaseInfo BuildDirectRelease()
        {
            return new ReleaseInfo
            {
                Title = SourceTitle,
                Indexer = Indexer,
                DownloadProtocol = DownloadProtocol.Direct,
                PublishDate = DateTime.Parse("2026-08-06T19:12:42Z").ToUniversalTime(),
                Size = 123456789
            };
        }

        private static ReleaseInfo BuildUsenetRelease()
        {
            return new ReleaseInfo
            {
                Title = SourceTitle,
                Indexer = Indexer,
                DownloadProtocol = DownloadProtocol.Usenet,
                PublishDate = DateTime.Parse("2026-08-06T19:12:42Z").ToUniversalTime(),
                Size = 123456789
            };
        }

        // The repository selects every mapped column, so the test table must cover them all.
        // Generating a superset from the model keeps this fixture from rotting when columns
        // are added; unmapped extras are harmless because inserts and selects never name them.
        private static string BuildCreateTableSql<TModel>(string tableName)
        {
            var columns = typeof(TModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => !p.PropertyType.IsGenericType ||
                            p.PropertyType.GetGenericTypeDefinition() != typeof(LazyLoaded<>))
                .Select(p => p.Name == "Id"
                    ? @"""Id"" INTEGER PRIMARY KEY"
                    : $@"""{p.Name}"" TEXT NULL");

            return $@"CREATE TABLE ""{tableName}"" ({string.Join(", ", columns)});";
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
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

                throw new NotImplementedException($"Test proxy does not implement {typeof(IAuthorService).Name}.{targetMethod?.Name}");
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
