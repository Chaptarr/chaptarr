using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class SeriesTagSyncFixture
    {
        private sealed class BookRefresh : RefreshBookService
        {
            public BookRefresh(Logger logger = null) : base(null, null, null, null, null, null, null, null, null, null, null, null, null, null, logger ?? LogManager.GetCurrentClassLogger(), null) { }
            public string Result(Book before, Book after) => UpdateEntity(before, after).ToString();
        }

        [TestCase(BookMediaType.Audiobook, false, false, false)]
        [TestCase(BookMediaType.Audiobook, true, false, true)]
        [TestCase(BookMediaType.Ebook, true, false, false)]
        [TestCase(BookMediaType.Audiobook, false, true, false)]
        public void header_sync_requires_a_changed_selected_pair(BookMediaType media, bool ambiguous, bool alreadyStored, bool expected)
        {
            var links = new List<SeriesBookLink> { new() { Position = "12", Series = new Series { Title = "Divine Apostasy", GoodreadsSeriesId = "gr:280137", MediaType = media } } };
            if (ambiguous) links.Add(new SeriesBookLink { Position = "2", Series = new Series { Title = "Other series", GoodreadsSeriesId = "gr:2", MediaType = media } });
            var before = new Book { Id = 1, Title = "Book", MediaType = media, SeriesId = alreadyStored ? 280137 : null, SeriesLinks = links, BookFiles = new List<BookFile>() };
            var after = new Book { Id = 1, Title = "Book", MediaType = media, SeriesId = 280137, SeriesLinks = links, BookFiles = new List<BookFile>() };
            var subject = new BookRefresh();
            Assert.That(subject.Result(before, after), Is.EqualTo(expected ? "UpdateTags" : alreadyStored ? "None" : "Standard"));
            Assert.That(subject.Result(before, after), Is.EqualTo("None"), "An unchanged header must not force another tag pass");
        }

        [Test]
        public void series_refresh_syncs_only_changed_audio_pairs_after_persistence(
            [Values(false, true)] bool postgres,
            [Values(WriteAudioTagsType.No, WriteAudioTagsType.NewFiles, WriteAudioTagsType.Sync)] WriteAudioTagsType mode,
            [Values("position", "title", "description", "ebook", "unchanged")] string change)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author { Name = "Author", CleanName = "author", TitleSlug = "author", Path = "/library", Added = DateTime.UtcNow });
                var repository = new BookRepository(database, events);
                var books = Enumerable.Range(1, 3).Select(id => repository.Insert(new Book { AuthorId = author.Id, Title = "Book " + id,
                    CleanTitle = "book" + id, TitleSlug = "book-" + id, HardcoverBookId = "hc:" + id, MediaType = id == 3 ? BookMediaType.Ebook : BookMediaType.Audiobook, Added = DateTime.UtcNow })).ToList();
                var links = new SeriesBookLinkService(new SeriesBookLinkRepository(database, events));
                var series = new SeriesService(new SeriesRepository(database, events, new SeriesBookLinkRepository(database, events), logger), logger);
                var linkReadIds = new List<List<int>>();
                var countedLinks = InterfaceProxy.Proxy<ISeriesBookLinkService>((method, args) =>
                {
                    if (method.Name == nameof(ISeriesBookLinkService.GetLinksByBook)) linkReadIds.Add(((List<int>)args[0]).ToList());
                    return method.Invoke(links, args);
                });
                var synced = new List<int>();
                var changedPhase = false;
                var catalog = InterfaceProxy.Proxy<IBookService>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetBooksByAuthor)));
                    BookSeries.LoadLinks(books, links.GetLinksByBook, reload: true);
                    return books;
                });
                var config = InterfaceProxy.Proxy<IConfigService>((_, _) => mode);
                var editions = InterfaceProxy.Proxy<IEditionService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IEditionService.GetEditionsByBook)));
                    return ((IEnumerable<int>)args[0]).Select(id => new Edition { Id = id, BookId = id, Book = books.Single(book => book.Id == id) }).ToList();
                });
                var audio = InterfaceProxy.Proxy<IAudioTagService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAudioTagService.SyncTags)));
                    var requested = (List<Edition>)args[0];
                    synced.AddRange(requested.Select(edition => edition.BookId));
                    Assert.That(requested.Select(edition => edition.Book.MediaType), Is.All.EqualTo(BookMediaType.Audiobook));
                    if (changedPhase && change == "position")
                        Assert.That(links.GetLinksByBook(new List<int> { books[0].Id }).Single().Position, Is.EqualTo("9"), "Sync must observe the saved link");
                    if (changedPhase && change == "title")
                        Assert.That(series.GetByAuthorId(author.Id).Single(row => row.MediaType == BookMediaType.Audiobook).Title, Is.EqualTo("Renamed series"), "Sync must observe the saved title");
                    return null;
                });
                var subject = new RefreshSeriesService(catalog, series, countedLinks, new RefreshSeriesBookLinkService(links, logger), logger, config, editions, audio);
                List<Series> Snapshot(bool changed) => new()
                {
                    new Series { Title = changed && change == "title" ? "Renamed series" : "Audio series", Description = changed && change == "description" ? "New description" : null,
                        GoodreadsSeriesId = "gr:1", MediaType = BookMediaType.Audiobook, SeriesBooks = new List<SeriesBook> {
                            new() { BookId = "hc:1", Position = changed && change == "position" ? "9" : "1" }, new() { BookId = "hc:2", Position = "2" } } },
                    new Series { Title = "Ebook series", GoodreadsSeriesId = "gr:2", MediaType = BookMediaType.Ebook,
                        SeriesBooks = new List<SeriesBook> { new() { BookId = "hc:3", Position = changed && change == "ebook" ? "9" : "1" } } }
                };
                subject.RefreshSeriesInfo(author.Id, Snapshot(false), author, false, false, null);
                synced.Clear();
                linkReadIds.Clear();
                changedPhase = true;
                subject.RefreshSeriesInfo(author.Id, Snapshot(true), author, false, false, null);
                var expected = mode == WriteAudioTagsType.Sync && change is "position" or "title"
                    ? books.Take(change == "title" ? 2 : 1).Select(book => book.Id).ToArray() : Array.Empty<int>();
                Assert.That(synced, Is.EquivalentTo(expected));
                Assert.That(linkReadIds.Count, Is.EqualTo(expected.Length > 0 ? 1 : 0));
                foreach (var ids in linkReadIds) Assert.That(ids, Is.EquivalentTo(expected), "Only affected audiobooks are reloaded, in one batch");
                synced.Clear();
                subject.RefreshSeriesInfo(author.Id, Snapshot(true), author, false, false, null);
                Assert.That(synced, Is.Empty, "Unchanged catalog must not repeat the tag pass");
            });
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void saved_series_changes_sync_other_authors_and_survive_a_later_failure(bool postgres, bool laterFailure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var first = authors.Insert(new Author { Name = "First author", CleanName = "first", TitleSlug = "first", Path = "/first", Added = DateTime.UtcNow });
                var second = authors.Insert(new Author { Name = "Second author", CleanName = "second", TitleSlug = "second", Path = "/second", Added = DateTime.UtcNow });
                var repository = new BookRepository(database, events);
                var books = new[] { first, second }.Select((author, index) => repository.Insert(new Book { AuthorId = author.Id, Title = "Book " + index,
                    CleanTitle = "book" + index, TitleSlug = "book-" + index, HardcoverBookId = "hc:" + (index + 1), MediaType = BookMediaType.Audiobook,
                    SeriesId = 43644, Added = DateTime.UtcNow })).ToList();
                var links = new SeriesBookLinkService(new SeriesBookLinkRepository(database, events));
                var series = new SeriesService(new SeriesRepository(database, events, new SeriesBookLinkRepository(database, events), logger), logger);
                var shared = new SeriesRepository(database, events, new SeriesBookLinkRepository(database, events), logger).Insert(new Series { Title = "The First Law", GoodreadsSeriesId = "gr:43644", MediaType = BookMediaType.Audiobook });
                var other = new SeriesRepository(database, events, new SeriesBookLinkRepository(database, events), logger).Insert(new Series { Title = "Other series", GoodreadsSeriesId = "gr:9", MediaType = BookMediaType.Audiobook });
                links.InsertMany(books.Select(book => new SeriesBookLink { BookId = book.Id, SeriesId = shared.Id, Position = "1" }).ToList());
                links.InsertMany(new List<SeriesBookLink> { new() { BookId = books[0].Id, SeriesId = other.Id, Position = "2" } });
                var catalog = InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    var selected = method.Name == nameof(IBookService.GetBooksByAuthor)
                        ? books.Where(book => book.AuthorId == (int)args[0]).ToList()
                        : books.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList();
                    BookSeries.LoadLinks(selected, links.GetLinksByBook, reload: true);
                    return selected;
                });
                var synced = new List<int>();
                var editions = InterfaceProxy.Proxy<IEditionService>((_, args) => ((IEnumerable<int>)args[0]).Select(id => new Edition { BookId = id }).ToList());
                var audio = InterfaceProxy.Proxy<IAudioTagService>((_, args) =>
                {
                    Assert.That(series.GetByAuthorId(second.Id).Single().Title, Is.EqualTo("Renamed series"));
                    synced.AddRange(((List<Edition>)args[0]).Select(edition => edition.BookId));
                    return null;
                });
                var refreshLinks = new RefreshSeriesBookLinkService(links, logger);
                var failingLinks = InterfaceProxy.Proxy<IRefreshSeriesBookLinkService>((method, args) =>
                {
                    if (laterFailure && ((List<SeriesBookLink>)args[5]).Any(link => link.Position == "3")) throw new InvalidOperationException("Later series failed");
                    return method.Invoke(refreshLinks, args);
                });
                var subject = new RefreshSeriesService(catalog, series, links, failingLinks, logger,
                    InterfaceProxy.Proxy<IConfigService>((_, _) => WriteAudioTagsType.Sync), editions, audio);
                var remote = new List<Series> {
                    new() { Title = "Renamed series", GoodreadsSeriesId = "gr:43644", MediaType = BookMediaType.Audiobook, SeriesBooks = new List<SeriesBook> { new() { BookId = "hc:1", Position = "1" } } },
                    new() { Title = "Other series", GoodreadsSeriesId = "gr:9", MediaType = BookMediaType.Audiobook, SeriesBooks = new List<SeriesBook> { new() { BookId = "hc:1", Position = laterFailure ? "3" : "2" } } }
                };
                if (laterFailure) Assert.That(() => subject.RefreshSeriesInfo(first.Id, remote, first, false, false, null), Throws.InvalidOperationException.With.Message.EqualTo("Later series failed"));
                else subject.RefreshSeriesInfo(first.Id, remote, first, false, false, null);
                Assert.That(synced, Is.EquivalentTo(books.Select(book => book.Id)), "A shared title change must reach both authors, even if a later series fails");
            });
        }
        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void removing_a_competing_membership_syncs_the_remaining_pair(bool postgres, bool wholeSeries)
        {
            CheckCapture(postgres, wholeSeries ? "delete-series" : "delete-link");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_captured_book_does_not_abort_the_saved_change(bool postgres)
        {
            CheckCapture(postgres, "missing-book");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_series_rename_does_not_load_other_authors_books_for_audio_sync(bool postgres)
        {
            CheckCapture(postgres, "ebook");
        }

        private static void CheckCapture(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var logs = new LogFactory();
                var warnings = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, warnings);
                logs.Configuration = logging;
                var logger = logs.GetLogger("SeriesCapture");
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authorRepo = new AuthorRepository(database, events, logger);
                var author = authorRepo.Insert(new Author { Name = "First", CleanName = "first", TitleSlug = "first", Path = "/first", Added = DateTime.UtcNow });
                var otherAuthor = authorRepo.Insert(new Author { Name = "Second", CleanName = "second", TitleSlug = "second", Path = "/second", Added = DateTime.UtcNow });
                var deletion = scenario.StartsWith("delete-", StringComparison.Ordinal);
                var media = scenario == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var bookRepo = new BookRepository(database, events);
                var books = Enumerable.Range(1, 2).Select(id => bookRepo.Insert(new Book {
                    AuthorId = deletion || id == 1 ? author.Id : otherAuthor.Id, Title = "Book " + id,
                    CleanTitle = "book" + id, TitleSlug = "book-" + id, HardcoverBookId = "hc:" + id,
                    MediaType = media, Added = DateTime.UtcNow })).ToList();
                var linkRepo = new SeriesBookLinkRepository(database, events);
                var links = new SeriesBookLinkService(linkRepo);
                var seriesRepo = new SeriesRepository(database, events, linkRepo, logger);
                var series = new SeriesService(seriesRepo, logger);
                var main = seriesRepo.Insert(new Series { Title = "Main series", GoodreadsSeriesId = "gr:1", MediaType = media });
                links.InsertMany(books.Select(book => new SeriesBookLink { BookId = book.Id, SeriesId = main.Id, Position = "2", SeriesPosition = 2 }).ToList());
                if (deletion)
                {
                    var competing = seriesRepo.Insert(new Series { Title = "Competing series", GoodreadsSeriesId = "gr:2", MediaType = media });
                    links.InsertMany(new List<SeriesBookLink> { new() { BookId = books[0].Id, SeriesId = competing.Id, Position = "1", SeriesPosition = 1 },
                        new() { BookId = books[1].Id, SeriesId = competing.Id, Position = "1", SeriesPosition = 1 } });
                }
                const int deletedBookId = 99999;
                var observedLinks = InterfaceProxy.Proxy<ISeriesBookLinkService>((method, args) =>
                {
                    var result = method.Invoke(links, args);
                    // Model a book deleted after the link snapshot; link cleanup runs later via an event.
                    if (scenario == "missing-book" && method.Name == nameof(ISeriesBookLinkService.GetLinksBySeries))
                        ((List<SeriesBookLink>)result).Add(new SeriesBookLink { BookId = deletedBookId, SeriesId = main.Id, Position = "3" });
                    return result;
                });
                var extraReads = 0;
                var catalog = InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    List<Book> selected;
                    if (method.Name == nameof(IBookService.GetBooksByAuthor)) selected = books.Where(book => book.AuthorId == (int)args[0]).ToList();
                    else
                    {
                        extraReads++;
                        var ids = (IEnumerable<int>)args[0];
                        selected = method.Name == nameof(IBookService.GetExistingBooks) ? bookRepo.FindExisting(ids).ToList() : bookRepo.Get(ids).ToList();
                    }
                    BookSeries.LoadLinks(selected, links.GetLinksByBook, reload: true);
                    return selected;
                });
                var synced = new List<int>();
                var editions = InterfaceProxy.Proxy<IEditionService>((_, args) => ((IEnumerable<int>)args[0]).Select(id => new Edition { BookId = id }).ToList());
                var audio = InterfaceProxy.Proxy<IAudioTagService>((_, args) => { synced.AddRange(((List<Edition>)args[0]).Select(edition => edition.BookId)); return null; });
                var subject = new RefreshSeriesService(catalog, series, observedLinks, new RefreshSeriesBookLinkService(links, logger), logger,
                    InterfaceProxy.Proxy<IConfigService>((_, _) => WriteAudioTagsType.Sync), editions, audio);
                var remote = new List<Series> { new() { Title = deletion ? main.Title : "Renamed series", GoodreadsSeriesId = "gr:1", MediaType = media,
                    SeriesBooks = books.Where(book => book.AuthorId == author.Id).Select(book => new SeriesBook { BookId = book.HardcoverBookId, Position = "2" }).ToList() } };
                if (scenario == "delete-link") remote.Add(new Series { Title = "Competing series", GoodreadsSeriesId = "gr:2", MediaType = media,
                    SeriesBooks = new List<SeriesBook> { new() { BookId = books[1].HardcoverBookId, Position = "1" } } });
                Assert.DoesNotThrow(() => subject.RefreshSeriesInfo(author.Id, remote, author, false, false, null));
                var expected = scenario == "ebook" ? Array.Empty<int>() : scenario == "delete-link" ? new[] { books[0].Id } : books.Select(book => book.Id).ToArray();
                Assert.That(synced, Is.EquivalentTo(expected));
                if (scenario == "ebook") Assert.That(extraReads, Is.Zero, "An ebook rename must not prepare an audio tag pass");
                if (scenario == "missing-book")
                {
                    Assert.That(extraReads, Is.EqualTo(1));
                    Assert.That(warnings.Logs, Does.Contain($"Series {main.Id}: skipped 1 missing book records while preparing audio tag sync"));
                }
                else Assert.That(warnings.Logs.Any(message => message.Contains("missing book records")), Is.False);
                BookSeries.LoadLinks(books, links.GetLinksByBook, reload: true);
                Assert.That(BookSeries.AudioTagPair(books[0]).Title, Is.EqualTo(deletion ? main.Title : "Renamed series"));
            });
        }
        [TestCase(false, false, "Standard")]
        [TestCase(true, false, "Standard")]
        [TestCase(false, true, "UpdateTags")]
        [TestCase(true, true, "UpdateTags")]
        public void author_refresh_local_reads_stored_links_for_a_header_transition(bool postgres, bool ambiguous, string expected)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                using var logs = new LogFactory();
                var messages = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                var config = new NLog.Config.LoggingConfiguration();
                config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                logs.Configuration = config;
                var logger = logs.GetLogger("HeaderComparison");
                var author = new AuthorRepository(database, events, logger).Insert(new Author { Name = "Author", CleanName = "author", TitleSlug = "author", Path = "/scratch", Added = DateTime.UtcNow });
                var stored = new BookRepository(database, events).Insert(new Book { AuthorId = author.Id, Title = "The Twelfth Conclave", CleanTitle = "thetwelfthconclave",
                    TitleSlug = "the-twelfth-conclave", HardcoverBookId = "hc:1", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow });
                var seriesRepo = new SeriesRepository(database, events, new SeriesBookLinkRepository(database, events), logger);
                var linkRepo = new SeriesBookLinkRepository(database, events);
                var divine = seriesRepo.Insert(new Series { Title = "Divine Apostasy", GoodreadsSeriesId = "gr:280137", MediaType = BookMediaType.Audiobook });
                linkRepo.Insert(new SeriesBookLink { BookId = stored.Id, SeriesId = divine.Id, Position = "12" });
                if (ambiguous)
                {
                    var other = seriesRepo.Insert(new Series { Title = "Other series", GoodreadsSeriesId = "gr:2", MediaType = BookMediaType.Audiobook });
                    linkRepo.Insert(new SeriesBookLink { BookId = stored.Id, SeriesId = other.Id, Position = "2" });
                }

                var sql = new List<string>();
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => sql.Add(command.CommandText)) : method.Invoke(database, args));

                var local = new BookRepository(observed, events).GetBooksForRefresh(author.Id, null).Single();
                Assert.That(local.LazySeriesLinks, Is.Not.Null, "Mapper must attach a prepared lazy proxy");
                Assert.That(local.LazySeriesLinks.IsLoaded, Is.False, "This path does not preload links");
                local.BookFiles = new List<BookFile>();
                sql.Clear();

                var remote = new Book { AuthorId = author.Id, Title = "The Twelfth Conclave", CleanTitle = "thetwelfthconclave", TitleSlug = "the-twelfth-conclave",
                    HardcoverBookId = "hc:1", MediaType = BookMediaType.Audiobook, SeriesId = 280137,
                    SeriesLinks = new List<SeriesBookLink> { new() { Position = "12", Series = new LazyLoaded<Series>(new Series { Title = "Divine Apostasy", GoodreadsSeriesId = "gr:280137", MediaType = BookMediaType.Audiobook }) } } };

                Assert.That(new BookRefresh(logger).Result(local, remote), Is.EqualTo(expected));
                Assert.That(sql.Any(text => text.Contains("\"SeriesBookLink\"")), Is.True, "UpdateEntity must read stored links from the database");
                Assert.That(messages.Logs.Single(message => message.StartsWith("Header series comparison", StringComparison.Ordinal)),
                    Does.Contain($": { (ambiguous ? 2 : 1)} stored links, 1 remote links, changed={ambiguous}, ").And.EndsWith(" ms"));
                messages.Logs.Clear();
                sql.Clear();
                Assert.That(new BookRefresh(logger).Result(local, remote), Is.EqualTo("None"));
                Assert.That(messages.Logs.Any(message => message.StartsWith("Header series comparison", StringComparison.Ordinal)), Is.False);
                Assert.That(sql.Any(text => text.Contains("\"SeriesBookLink\"") || text.Contains("\"Series\"")), Is.False);
            });
        }
    }
}
