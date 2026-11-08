using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    public partial class RefreshBookServiceMergeEditionSelectionFixture
    {
        private sealed class CatalogRefresh : RefreshAuthorService
        {
            public CatalogRefresh(IAuthorService authors, IBookService books, IEditionService editions, IRefreshBookService refresh,
                IMediaFileService files, IEventAggregator events, IMainDatabase database, IAuthorSyncMetadataService sync, NLog.Logger logger)
                : base(MetadataHealthGateStub.Ready(), null, authors, books, editions, null, refresh, null, events, null, files, new global::NzbDrone.Core.History.HistoryRepository(database, events), null, null, null, null,
                    null, sync, null, null, logger, mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger)) { }
            public bool FailChildren { get; set; }
            protected override bool RefreshChildren(SortedChildren children, List<Book> remote, Author author, bool force, bool tags, DateTime? last, RefreshContext context)
            {
                if (FailChildren) throw new InvalidOperationException("planted child failure after AddChildren");
                return base.RefreshChildren(children, remote, author, force, tags, last, context);
            }
            protected override List<Book> GetRemoteChildren(Author local, Author remote, RefreshContext context) => remote.Books;
        }

        private static CatalogRefresh CreateCatalogRefresh(IMainDatabase database, IdentityEvents events, IAuthorSyncMetadataService sync = null, NLog.Logger logger = null)
        {
            var aggregator = events.Aggregator();
            var authors = new AuthorRepository(database, aggregator, Logger);
            var books = new BookRepository(database, aggregator);
            var editionService = new EditionService(new EditionRepository(database, aggregator), aggregator, Logger);
            var files = new MediaFileRepository(database, aggregator);
            Book Load(int id)
            {
                var book = books.Get(id);
                book.Author = authors.Get(book.AuthorId);
                book.Editions = editionService.GetEditionsByBook(id);
                book.BookFiles = files.GetFilesByBook(id);
                return book;
            }
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name switch
            {
                nameof(IBookService.GetBooksForRefresh) => books.GetBooksForRefresh((int)args[0], null).Select(book => Load(book.Id)).ToList(),
                nameof(IBookService.GetBooksByAuthorId) or nameof(IBookService.GetBooksByAuthor) => books.GetBooksByAuthorId((int)args[0]).Select(book => Load(book.Id)).ToList(),
                nameof(IBookService.GetBook) => Load((int)args[0]),
                nameof(IBookService.UpdateMany) => SaveBooks((List<Book>)args[0]),
                nameof(IBookService.DeleteBook) => DeleteBook((int)args[0], (bool)args[1]),
                    nameof(IBookService.InsertMany) => InsertBooks((List<Book>)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            object DeleteBook(int id, bool deleteFiles) { Assert.That(deleteFiles, Is.False); books.DeleteManyAtomic(new[] { id }); return null; }
            object SaveBooks(List<Book> rows) { books.UpdateMany(rows); return null; }
            object InsertBooks(List<Book> rows) { books.InsertMany(rows); return rows; }
            var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
            {
                nameof(IAuthorService.GetAllAuthors) => authors.All().ToList(),
                nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                nameof(IAuthorService.UpdateAuthor) => authors.Update((Author)args[0]),
                nameof(IAuthorService.SetAddOptions) => SaveOptions((Author)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            object SaveOptions(Author row) { authors.SetFields(row, a => a.AddOptions); return null; }
            var mediaService = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
            {
                nameof(IMediaFileService.GetFilesByBooks) => files.GetFilesByBooks((List<int>)args[0]),
                nameof(IMediaFileService.GetFilesByBook) => files.GetFilesByBook((int)args[0]),
                nameof(IMediaFileService.GetFilesByEdition) => files.GetFilesByEdition((int)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            var tags = InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null);
            var refreshBook = new RefreshBookService(bookService, authorService, null, editionService, null, null,
                new RefreshEditionService(editionService, tags, null, database, Logger), mediaService, null, aggregator, null,
                new EditionSelector(Logger), new EditionMetadataProfileFilter(new TestTermMatcherService()), null, Logger, database);
            return new CatalogRefresh(authorService, bookService, editionService, refreshBook, mediaService, aggregator, database, sync, logger ?? Logger);
        }

        [Test]
        public void two_full_refresh_passes_keep_identity_and_file_links_stable_with_real_book_refresh(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var events = new IdentityEvents();
                var aggregator = events.Aggregator();
                var authors = new AuthorRepository(database, aggregator, Logger);
                var books = new BookRepository(database, aggregator);
                var editionService = new EditionService(new EditionRepository(database, aggregator), aggregator, Logger);
                var files = new MediaFileRepository(database, aggregator);
                library.Source.MediaType = library.Target.MediaType = media;
                library.Source.GoodreadsWorkId = "gr:111";
                library.Source.RemoteProviderIds = new() { "gr:111" };
                books.UpdateMany(new[] { library.Source, library.Target });
                library.SourceEdition.ReadingFormatId = library.TargetEdition.ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3;
                editionService.UpdateMany(new() { library.SourceEdition, library.TargetEdition });
                var author = authors.Get(library.Source.AuthorId);
                author.Overview = "";
                author.Path = TestContext.CurrentContext.TestDirectory;
                authors.Update(author);
                var refresh = CreateCatalogRefresh(database, events);
                var served = RefreshEntityCopy.CloneBook(books.Get(library.Target.Id), false);
                served.Id = 0;
                served.Editions = new() { RefreshEntityCopy.CloneEdition(library.SourceEdition), RefreshEntityCopy.CloneEdition(library.TargetEdition) };
                foreach (var edition in served.Editions) { edition.Id = 0; edition.BookId = 0; }
                var published = authors.Get(author.Id);
                published.Books = new() { served };
                refresh.ReconcileAuthorBlob(authors.Get(author.Id), published);
                using var connection = database.OpenConnection();
                var before = connection.Query<string>("SELECT \"Id\" || ':' || \"ProviderIds\" FROM \"Books\" ORDER BY \"Id\"").ToArray();
                Assert.That(books.Find(library.Target.Id), Is.Null);
                Assert.That(books.Get(library.Source.Id).GoodreadsWorkId, Is.EqualTo(served.GoodreadsWorkId));
                var identityEvents = events.Changed.Count;
                // Audit actual changed identity/ownership, excluding normal LastInfoSync stamps.
                connection.Execute("CREATE TABLE identity_writes (value INTEGER);");
                connection.Execute(postgres
                    ? "CREATE FUNCTION count_book_identity_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"ProviderIds\" IS DISTINCT FROM OLD.\"ProviderIds\" THEN INSERT INTO identity_writes VALUES (1); END IF; RETURN NEW; END $$; CREATE TRIGGER count_book_identity_write AFTER UPDATE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION count_book_identity_write();"
                    : "CREATE TRIGGER count_book_identity_write AFTER UPDATE ON \"Books\" WHEN NEW.\"ProviderIds\" IS NOT OLD.\"ProviderIds\" BEGIN INSERT INTO identity_writes VALUES (1); END;");
                refresh.ReconcileAuthorBlob(authors.Get(author.Id), published);
                Assert.Multiple(() =>
                {
                    Assert.That(connection.Query<string>("SELECT \"Id\" || ':' || \"ProviderIds\" FROM \"Books\" ORDER BY \"Id\""), Is.EqualTo(before));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM identity_writes"), Is.Zero);
                    Assert.That(events.Changed.Count, Is.EqualTo(identityEvents));
                    Assert.That(files.Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(editionService.GetEdition(library.SourceEdition.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(events.Deleted, Is.Empty);
                });
            });
        }

        [Test]
        public void contested_filed_row_adopts_its_edition_pocket_and_refresh_settles_in_one_pass(
            [Values(false, true)] bool postgres, [Values(false, true)] bool reverse, [Values(false, true)] bool rotation)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var editions = new EditionRepository(database, null);
                var authors = new AuthorRepository(database, null, Logger);
                var author = authors.Get(library.Source.AuthorId);
                author.Overview = "";
                author.Path = TestContext.CurrentContext.TestDirectory;
                author.AddOptions = new AddAuthorOptions { AwaitingMetadata = true, AudiobookAwaitingMetadata = true };
                authors.Update(author);
                library.Source.GoodreadsWorkId = "gr:111";
                library.Source.RemoteProviderIds = rotation ? new() { "gr:111" } : new() { "gr:111", "gr:222" };
                library.Source.WantedNarratorId = 27;
                library.Source.LastSearchTime = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
                library.Source.AddOptions = new AddBookOptions { SearchForNewBook = false };
                books.Update(library.Source);
                editions.Delete(library.TargetEdition.Id);
                books.Delete(library.Target.Id);
                Book Pocket(string id, string title, Edition edition) => new Book
                {
                    AuthorId = author.Id, Title = title, CleanTitle = title, TitleSlug = id.Replace(':', '-'),
                    GoodreadsWorkId = id, RemoteProviderIds = new() { id }, AnyEditionOk = true,
                    Editions = new() { edition }, Added = DateTime.UtcNow
                };
                var served = authors.Get(author.Id);
                var filedEdition = RefreshEntityCopy.CloneEdition(library.SourceEdition);
                filedEdition.Id = 0;
                filedEdition.BookId = 0;
                served.Books = new()
                {
                    Pocket("gr:111", "Dune Messiah", new Edition { Title = "Dune Messiah", ForeignEditionId = "gr:999", GoodreadsEditionId = 999, ReadingFormatId = 2 }),
                    Pocket("gr:222", "Dune", filedEdition)
                };
                if (reverse) served.Books.Reverse();
                var events = new IdentityEvents();
                var refresh = CreateCatalogRefresh(database, events);
                Assert.DoesNotThrow(() => refresh.ReconcileAuthorBlob(authors.Get(author.Id), served));
                var saved = books.Get(library.Source.Id);
                var identities = books.All().OrderBy(book => book.Id).Select(book => $"{book.Id}:{book.GoodreadsWorkId}").ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(books.All(), Has.Count.EqualTo(2));
                    Assert.That(saved.GoodreadsWorkId, Is.EqualTo("gr:222"));
                    Assert.That(saved.RemoteProviderIds, Does.Not.Contain("gr:111"));
                    Assert.That(saved.WantedNarratorId, Is.EqualTo(27));
                    Assert.That(saved.LastSearchTime, Is.EqualTo(library.Source.LastSearchTime));
                    Assert.That(saved.AddOptions.SearchForNewBook, Is.False);
                    Assert.That(saved.AudiobookMonitored, Is.True);
                    Assert.That(new MediaFileRepository(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(editions.Get(library.SourceEdition.Id).BookId, Is.EqualTo(saved.Id));
                });
                Assert.DoesNotThrow(() => refresh.ReconcileAuthorBlob(authors.Get(author.Id), served));
                Assert.That(books.All().OrderBy(book => book.Id).Select(book => $"{book.Id}:{book.GoodreadsWorkId}"), Is.EqualTo(identities));
            });
        }

        [Test]
        public void failed_refresh_publishes_committed_children_without_acknowledging_setup(
            [Values(false, true)] bool postgres, [Values(false, true)] bool repairFailure,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var authors = new AuthorRepository(database, null, Logger);
                library.Source.MediaType = library.Target.MediaType = media;
                books.UpdateMany(new[] { library.Source, library.Target });
                library.SourceEdition.ReadingFormatId = library.TargetEdition.ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3;
                new EditionRepository(database, null).UpdateMany(new[] { library.SourceEdition, library.TargetEdition });
                var withdrawn = books.Insert(new Book { AuthorId = library.Source.AuthorId, Title = "Withdrawn work", CleanTitle = "withdrawnwork", TitleSlug = "withdrawn-work", GoodreadsWorkId = "gr:555", MediaType = media, AnyEditionOk = true, Added = DateTime.UtcNow });
                var author = authors.Get(library.Source.AuthorId);
                author.Overview = "";
                author.HardcoverAuthorId = "hc:123";
                author.Path = TestContext.CurrentContext.TestDirectory;
                author.AddOptions = new AddAuthorOptions { AwaitingMetadata = true, AudiobookAwaitingMetadata = true };
                authors.Update(author);
                library.Source.Editions = new() { library.SourceEdition };
                if (!repairFailure)
                {
                    library.Target.GoodreadsWorkId = "gr:999";
                    library.Target.RemoteProviderIds = new() { "gr:999" };
                    books.Update(library.Target);
                }
                var served = authors.Get(author.Id);
                served.Books = new()
                {
                    RefreshEntityCopy.CloneBook(library.Source, true),
                    new() { Title = "Children of Dune", CleanTitle = "childrenofdune", TitleSlug = "children-of-dune", GoodreadsWorkId = "gr:333", AnyEditionOk = true, MediaType = media,
                        Editions = new() { new Edition { Title = "Children of Dune", ForeignEditionId = "gr:444", GoodreadsEditionId = 444, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 } } }
                };
                served.Books[0].Overview = "Updated server description";
                foreach (var book in served.Books) { book.Id = 0; foreach (var edition in book.Editions) { edition.Id = 0; edition.BookId = 0; } }
                using var connection = database.OpenConnection();
                if (repairFailure)
                    connection.Execute(postgres
                        ? $"CREATE FUNCTION abort_repair() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD.\"Id\" = {library.Target.Id} THEN RAISE EXCEPTION 'planted ownership failure'; END IF; RETURN OLD; END; $$; CREATE TRIGGER abort_repair BEFORE DELETE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION abort_repair();"
                        : $"CREATE TRIGGER abort_repair BEFORE DELETE ON \"Books\" WHEN OLD.\"Id\" = {library.Target.Id} BEGIN SELECT RAISE(ABORT, 'planted ownership failure'); END;");
                var syncRows = new AuthorSyncMetadataRepository(database, null);
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = author.Id, ExternalAuthorId = "hc:123", ETag = "old" });
                var sync = new AuthorSyncMetadataService(syncRows, Logger);
                var events = new IdentityEvents();
                using var factory = new NLog.LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
                var config = new NLog.Config.LoggingConfiguration();
                config.AddRuleForAllLevels(sink);
                factory.Configuration = config;
                var refresh = CreateCatalogRefresh(database, events, sync, factory.GetLogger("refresh-failure"));
                refresh.FailChildren = !repairFailure;
                var method = typeof(RefreshAuthorService).GetMethod("ApplyFetchedAuthor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var result = RefreshResult.Updated(served);
                result.ETag = "must-not-ack";
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(refresh, new object[] { authors.Get(author.Id), result, System.Threading.CancellationToken.None }));
                var added = books.All().Single(book => book.GoodreadsWorkId == "gr:333");
                Assert.Multiple(() =>
                {
                    Assert.That(error.ToString(), Does.Contain(repairFailure ? "planted ownership failure" : "planted child failure"));
                    Assert.That(events.Refreshed, Has.Count.EqualTo(1));
                    Assert.That(events.Refreshed.Single().Added.Select(book => book.Id), Does.Contain(added.Id));
                    Assert.That(events.Refreshed.Single().Updated.Select(book => book.Id), repairFailure ? Does.Contain(library.Source.Id) : Is.Empty);
                    Assert.That(events.Refreshed.Single().Removed.Select(book => book.Id), repairFailure ? Is.EquivalentTo(new[] { withdrawn.Id }) : Is.Empty);
                    Assert.That(books.Find(withdrawn.Id), repairFailure ? Is.Null : Is.Not.Null);
                    Assert.That(books.Get(library.Source.Id).Overview, repairFailure ? Is.EqualTo("Updated server description") : Is.Not.EqualTo("Updated server description"));
                    Assert.That(books.Find(library.Target.Id), Is.Not.Null);
                    Assert.That(authors.Get(author.Id).AddOptions.AwaitingMetadata, Is.True);
                    Assert.That(syncRows.All().Single().ETag, Is.Not.EqualTo("must-not-ack"));
                    Assert.That(syncRows.All().Single().LastSyncStatus, Is.EqualTo(SyncStatus.Failed));
                    Assert.That(new MediaFileRepository(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                });
                if (repairFailure)
                {
                    Assert.That(added.LastInfoSync, Is.GreaterThan(DateTime.MinValue), "other books refresh even after the ownership repair rolled back");
                    Assert.That(string.Join("\n", sink.Logs), Does.Contain("Could not repair edition ownership").And.Contain("planted ownership failure"));
                }
            });
        }
    }
}
