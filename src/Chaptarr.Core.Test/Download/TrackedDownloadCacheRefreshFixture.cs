using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TrackedDownloadCacheRefreshFixture
    {
        [Test]
        public void committed_identity_refresh_reloads_targets_without_search_and_retries_database_failure_on_next_poll([Values(false, true)] bool failRead)
        {
            var cache = new CacheManager();
            var primary = new Author { Id = 626, Name = "Linda Kay Silva" };
            var book = new Book { Id = 41994, AuthorId = primary.Id, Author = primary, MediaType = BookMediaType.Audiobook };
            var history = new EntityHistory { AuthorId = primary.Id, Author = primary, BookId = book.Id, Book = book,
                EditionId = 107188, DownloadId = "identity-grab", EventType = EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), Data = new() };
            var reads = 0;
            var service = new TrackedDownloadService(Proxy<IParsingService>((method, args) => throw new AssertionException("Identity repair must not search by title")), cache,
                Proxy<IHistoryService>((method, args) => { reads++; if (failRead) throw new InvalidOperationException("database unavailable"); return new List<EntityHistory> { history }; }),
                Proxy<IEventAggregator>((method, args) => null), null,
                Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()), LogManager.GetCurrentClassLogger(), null);
            var item = new TrackedDownload { State = TrackedDownloadState.ImportBlocked, DownloadItem = new DownloadClientItem { DownloadId = "identity-grab" },
                RemoteBook = new RemoteBook { Author = new Author { Id = 518 }, Books = new() { new() { Id = 33659, AuthorId = 518, MediaType = BookMediaType.Audiobook } },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) }, Release = new ReleaseInfo { Title = "The Horde", IndexerId = 7 } } };
            var entries = cache.GetCache<TrackedDownload>(service.GetType());
            entries.Set("identity-grab", item);
            service.Handle(new LibraryIdentityChangedEvent(new[] { 518, 626 }, new[] { 33659, 41994 }, new[] { 33659 }));
            Assert.That(reads, Is.EqualTo(1));
            if (failRead) Assert.That(entries.Find("identity-grab"), Is.Null, "Next normal client poll rebuilds from durable history");
            else
            {
                Assert.That(item.RemoteBook.Author.Id, Is.EqualTo(primary.Id));
                Assert.That(item.RemoteBook.Books.Single().Id, Is.EqualTo(book.Id));
                Assert.That(item.RemoteBook.TargetEditionIds[book.Id], Is.EqualTo(history.EditionId));
                Assert.That(item.RemoteBook.Release.IndexerId, Is.EqualTo(7));
                Assert.That(item.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            }
        }

        [TestCase(false, 1, true, false)]
        [TestCase(false, 25, true, false)]
        [TestCase(true, 1, true, false)]
        [TestCase(true, 25, true, false)]
        [TestCase(false, 1, false, false)]
        [TestCase(false, 25, false, false)]
        [TestCase(true, 1, false, false)]
        [TestCase(true, 25, false, false)]
        [TestCase(false, 2, true, true)]
        [TestCase(true, 2, true, true)]
        public void removed_collection_members_should_preserve_surviving_targets_and_metadata_once_per_download(bool postgres, int count, bool history, bool failFirstFormat)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = 0;
                var eventService = Proxy<IEventAggregator>((method, args) => { events++; return null; });
                var authors = new AuthorRepository(database, eventService, logger);
                var author = authors.Insert(new Author { Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", SortNameLastFirst = "Writer, Test", TitleSlug = "test-writer", Added = DateTime.UtcNow, Path = "/test-writer", HardcoverAuthorId = "hc:38" });
                var bookRepository = new BookRepository(database, eventService);
                Book Insert(string id, BookMediaType mediaType) => bookRepository.Insert(new Book { AuthorId = author.Id, HardcoverBookId = "hc:" + id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = id, Added = DateTime.UtcNow, MediaType = mediaType });
                var removed = Insert("removed", BookMediaType.Audiobook);
                var removedSecond = Insert("removed-second", BookMediaType.Audiobook);
                var survivor = Insert("survivor", BookMediaType.Audiobook);
                var independent = Insert("independent-copy", BookMediaType.Audiobook);
                var ebook = Insert("other-media", BookMediaType.Ebook);
                var native = new HistoryRepository(database, eventService);
                var cache = new CacheManager();
                var reads = 0;
                var maps = 0;
                var calculations = 0;
                var historyService = Proxy<IHistoryService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHistoryService.FindByDownloadIds)));
                    reads++;
                    return native.FindByDownloadIds(((IEnumerable<string>)args[0]).ToList(), (EntityHistoryEventType)args[1]);
                });
                var parsing = Proxy<IParsingService>((method, args) =>
                {
                    maps++;
                    return new RemoteBook { Author = author, Books = new List<Book> { independent, ebook }, ParsedBookInfo = (ParsedBookInfo)args[0] };
                });
                var service = new TrackedDownloadService(parsing, cache, historyService, eventService, null,
                    Proxy<ICustomFormatCalculationService>((method, args) =>
                    {
                        calculations++;
                        if (failFirstFormat && calculations == 1) throw new InvalidOperationException("Format calculator unavailable");
                        return new List<CustomFormat>();
                    }), logger, null);
                var tracked = new List<TrackedDownload>();
                for (var index = 0; index < count; index++)
                {
                    var downloadId = "cache-" + index;
                    if (history)
                    {
                        foreach (var book in new[] { removed, removedSecond, survivor }) native.Insert(new EntityHistory
                        {
                            DownloadId = downloadId, AuthorId = author.Id, BookId = book.Id, SourceTitle = "Test Writer - Paper Lanterns [M4B]", EventType = EntityHistoryEventType.Grabbed,
                            Date = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B)
                        });
                    }

                    var item = new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked, Indexer = "Saved Indexer", Added = DateTime.UtcNow.AddHours(-1),
                        DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "renamed-client-item" },
                        RemoteBook = new RemoteBook
                        {
                            Author = author, Books = new List<Book> { removed, null, removedSecond, survivor }, ReleaseSource = ReleaseSourceType.InteractiveSearch,
                            ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B), Narrator = "Saved Narrator", IsGraphicAudio = true },
                            Release = new ReleaseInfo { Title = "Original Selected Release", Indexer = "Saved Indexer", IndexerFlags = IndexerFlags.Freeleech }
                        }
                    };
                    tracked.Add(item);
                    cache.GetCache<TrackedDownload>(service.GetType()).Set(downloadId, item);
                }

                bookRepository.DeleteMany(new[] { removed.Id, removedSecond.Id });
                events = 0;
                var timer = Stopwatch.StartNew();
                service.Handle(new BookInfoRefreshedEvent(author, new List<Book>(), new List<Book>(), new List<Book> { removed, removedSecond }));
                TestContext.Progress.WriteLine($"TRACKED_CACHE database={database.DatabaseType} downloads={count} history={history} historyReads={reads} titleMaps={maps} formatCalculations={calculations} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                Assert.Multiple(() =>
                {
                    Assert.That(reads, Is.EqualTo(1));
                    Assert.That(maps, Is.Zero);
                    Assert.That(calculations, Is.EqualTo(count));
                    Assert.That(events, Is.EqualTo(1));
                    foreach (var item in tracked)
                    {
                        Assert.That(item.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { survivor.Id }));
                        Assert.That(item.RemoteBook.Release.Title, Is.EqualTo("Original Selected Release"));
                        Assert.That(item.RemoteBook.ParsedBookInfo.Narrator, Is.EqualTo("Saved Narrator"));
                        Assert.That(item.RemoteBook.ParsedBookInfo.IsGraphicAudio, Is.True);
                        Assert.That(item.RemoteBook.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                        Assert.That(item.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    }
                });
            });
        }

        [Test]
        public void deleting_a_tracked_owner_should_clear_targets_without_reassigning_by_title()
        {
            var author = new Author { Id = 38, Name = "Test Writer" };
            var cache = new CacheManager();
            var calls = 0;
            var service = new TrackedDownloadService(Proxy<IParsingService>((method, args) => { calls++; throw new AssertionException("Deleted owner must not be remapped"); }),
                cache, null, Proxy<IEventAggregator>((method, args) => null), null, null, LogManager.GetCurrentClassLogger(), null);
            var remote = new RemoteBook { Author = author, Books = new List<Book> { new() { Id = 501, AuthorId = author.Id } }, Release = new ReleaseInfo { Title = "Saved Release" } };
            var item = new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = "deleted-owner", Title = "Test Writer - Paper Lanterns" }, RemoteBook = remote };
            cache.GetCache<TrackedDownload>(service.GetType()).Set(item.DownloadItem.DownloadId, item);
            service.Handle(new AuthorDeletedEvent(author, false, false));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.Zero);
                Assert.That(item.RemoteBook.Author, Is.Null);
                Assert.That(item.RemoteBook.Books, Is.Empty);
                Assert.That(item.RemoteBook.Release.Title, Is.EqualTo("Saved Release"));
            });
        }

        [TestCase(null)]
        [TestCase("")]
        public void missing_download_ids_should_preserve_surviving_targets_without_title_mapping(string downloadId)
        {
            var author = new Author { Id = 38 };
            var removed = new Book { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var survivor = new Book { Id = 502, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var cache = new CacheManager();
            var reads = 0;
            var service = new TrackedDownloadService(Proxy<IParsingService>((method, args) => throw new AssertionException("Known targets must not rematch by title")), cache,
                Proxy<IHistoryService>((method, args) => { reads++; return new List<EntityHistory>(); }), Proxy<IEventAggregator>((method, args) => null), null,
                Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()), LogManager.GetCurrentClassLogger(), null);
            var item = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "" },
                RemoteBook = new RemoteBook { Author = author, Books = new List<Book> { removed, survivor }, ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) } }
            };
            cache.GetCache<TrackedDownload>(service.GetType()).Set("cache-key", item);
            service.Handle(new BookInfoRefreshedEvent(author, new List<Book>(), new List<Book>(), new List<Book> { removed }));
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(item.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { survivor.Id }));
        }

        [Test]
        public void deleting_all_captured_targets_should_not_adopt_same_title_replacements()
        {
            var author = new Author { Id = 38, Name = "Test Writer" };
            var removed = new Book { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var survivor = new Book { Id = 502, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var ebook = new Book { Id = 503, AuthorId = author.Id, MediaType = BookMediaType.Ebook };
            var cache = new CacheManager();
            var maps = 0;
            var service = new TrackedDownloadService(Proxy<IParsingService>((method, args) =>
            {
                maps++;
                return new RemoteBook { Author = author, Books = new List<Book> { survivor, ebook } };
            }), cache, Proxy<IHistoryService>((method, args) => new List<EntityHistory>()), Proxy<IEventAggregator>((method, args) => null), null,
                Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()), LogManager.GetCurrentClassLogger(), null);
            var item = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "all-targets-gone", Title = "Test Writer - Paper Lanterns [M4B]" },
                RemoteBook = new RemoteBook { Author = author, Books = new List<Book> { removed }, ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) } }
            };
            cache.GetCache<TrackedDownload>(service.GetType()).Set(item.DownloadItem.DownloadId, item);
            service.Handle(new BookInfoRefreshedEvent(author, new List<Book>(), new List<Book>(), new List<Book> { removed }));
            Assert.That(maps, Is.Zero);
            Assert.That(item.RemoteBook.Books, Is.Empty);
        }

        [TestCase(false, false, false)] [TestCase(true, false, false)]
        [TestCase(false, true, false)] [TestCase(true, true, false)]
        [TestCase(false, true, true)] [TestCase(true, true, true)]
        public void public_tracking_should_restore_raw_narrator_evidence_from_grab_history_without_replacing_current_evidence(bool postgres, bool useStoredRelease, bool preserveCurrent)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                { Name = "Test Writer", CleanName = "testwriter", TitleSlug = "test-writer", Path = "/fixture", Added = DateTime.UtcNow });
                var book = new BookRepository(database, events).Insert(new Book
                { AuthorId = author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = "paper-lanterns", Added = DateTime.UtcNow, MediaType = BookMediaType.Audiobook });
                var edition = new BasicRepository<Edition>(database, events).Insert(new Edition { BookId = book.Id, Title = book.Title, Monitored = true });
                book.Author = author; book.Editions = new() { edition };
                var raw = new List<string> { "Reader One; unclassified source text", "Reader Two; other source text" };
                var property = typeof(ReleaseInfo).GetProperty("NarratorEvidenceFields");
                var release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns [M4B]", Guid = "raw-evidence", IndexerId = 7, DownloadProtocol = DownloadProtocol.Torrent };
                property?.SetValue(release, raw);
                var remote = new RemoteBook { Author = author, Books = new() { book }, Release = release,
                    ParsedBookInfo = new ParsedBookInfo { BookTitle = book.Title, AuthorName = author.Name, Quality = new QualityModel(Quality.M4B) } };
                var grabbed = new BookGrabbedEvent(remote) { DownloadId = "raw-evidence", DownloadClient = "Offline client" };
                var history = new HistoryService(new HistoryRepository(database, events), logger);
                var downloadHistory = new DownloadHistoryService(new DownloadHistoryRepository(database, events), history);
                history.Handle(grabbed); downloadHistory.Handle(grabbed);
                var current = new List<string> { "current raw source" };
                var parser = Proxy<IParsingService>((method, args) =>
                {
                    var mapped = new RemoteBook { ParsedBookInfo = (ParsedBookInfo)args[0] };
                    if (args.Length == 3)
                    {
                        mapped.Author = author; mapped.Books = new() { book }; mapped.Release = new ReleaseInfo();
                        if (preserveCurrent) property?.SetValue(mapped.Release, current);
                    }
                    return mapped;
                });
                var historyReader = Proxy<IDownloadHistoryService>((method, args) => method.Name == nameof(IDownloadHistoryService.GetLatestGrab) && useStoredRelease
                    ? downloadHistory.GetLatestGrab((string)args[0]) : null);
                var service = new TrackedDownloadService(parser, new CacheManager(), history, events, historyReader,
                    Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()), logger,
                    Proxy<IDownloadClientFileSnapshotService>((method, args) => null));
                var tracked = service.TrackDownload(new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent },
                    new DownloadClientItem { DownloadId = grabbed.DownloadId, Title = release.Title,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Offline client", Protocol = DownloadProtocol.Torrent } });
                Assert.That(tracked.RemoteBook.Books.Select(value => value.Id), Is.EqualTo(new[] { book.Id }));
                var restored = property?.GetValue(tracked.RemoteBook.Release) as List<string>;
                Assert.That(restored, Is.EqualTo(preserveCurrent ? current : raw));
                Assert.That(Json.Deserialize<List<string>>(history.FindByDownloadId(grabbed.DownloadId).Single().GetDataValue("NarratorEvidenceFields")), Is.EqualTo(raw));
                Assert.That(tracked.RemoteBook.PositivelyIncompatibleBookIds, Is.Null);
            });
        }
    }
}
