using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Common;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public partial class RefreshBookServiceMergeEditionSelectionFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private sealed class Library
        {
            public IMainDatabase Database;
            public Book Source, Target;
            public Edition SourceEdition, TargetEdition;
            public BookFile File;
            public Library(IMainDatabase database, bool sameEdition)
            {
                Database = database;
                var author = new BasicRepository<Author>(database, null).Insert(new Author
                { Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert", TitleSlug = "frank-herbert", Path = "/library/Frank Herbert", Added = DateTime.UtcNow });
                var books = new BookRepository(database, null);
                Source = books.Insert(new Book { AuthorId = author.Id, Title = "Dune", CleanTitle = "dune", TitleSlug = "dune-source", GoodreadsWorkId = "gr:3634639", AudiobookMonitored = true, AnyEditionOk = true, Added = DateTime.UtcNow });
                Target = books.Insert(new Book { AuthorId = author.Id, Title = "Dune", CleanTitle = "dune", TitleSlug = "dune-target", GoodreadsWorkId = "gr:3634639", AnyEditionOk = true, Added = DateTime.UtcNow });
                var editions = new BasicRepository<Edition>(database, null);
                SourceEdition = editions.Insert(new Edition { BookId = Source.Id, Title = "Dune", ForeignEditionId = "gr:44767458-audiobook", GoodreadsEditionId = 44767458, ReadingFormatId = 2, Narrator = "Scott Brick", Monitored = true });
                TargetEdition = editions.Insert(new Edition { BookId = Target.Id, Title = "Dune", ForeignEditionId = sameEdition ? SourceEdition.ForeignEditionId : "gr:234225-audiobook", GoodreadsEditionId = sameEdition ? 44767458 : 234225, ReadingFormatId = 2, Narrator = sameEdition ? "Scott Brick" : "George Guidall", Monitored = true });
                File = new BasicRepository<BookFile>(database, null).Insert(new BookFile { EditionId = SourceEdition.Id, Path = "/library/Frank Herbert/Dune/01.mp3", Size = 1234, Quality = new QualityModel(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                new HistoryRepository(database, null).Insert(new EntityHistory { AuthorId = author.Id, BookId = Source.Id, EditionId = SourceEdition.Id, DownloadId = "preserved-torrent", EventType = EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow, SourceTitle = "Dune", Quality = new QualityModel(), Data = new() { ["IndexerId"] = "7" } });
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""DownloadHistory"" (""AuthorId"", ""BookId"", ""DownloadId"", ""SourceTitle"", ""Date"", ""EventType"", ""IndexerId"") VALUES (@AuthorId, @BookId, 'preserved-torrent', 'Dune', @date, 1, 7);
                    INSERT INTO ""Blacklist"" (""AuthorId"", ""SourceTitle"", ""Date"", ""Quality"", ""BookIds"") VALUES (@AuthorId, 'other-release', @date, '{}', @ids);
                    INSERT INTO ""PendingReleases"" (""AuthorId"", ""Title"", ""Added"", ""Release"", ""Reason"", ""AdditionalInfo"") VALUES (@AuthorId, 'Dune', @date, '{}', 0, @targets);",
                    new { AuthorId = author.Id, BookId = Source.Id, date = DateTime.UtcNow, ids = $"[{Source.Id}]", targets = $"{{\"bookIds\":[{Source.Id}],\"targetEditionIds\":{{\"{Source.Id}\":{SourceEdition.Id}}}}}" });
            }
            public void Merge(bool abort = false)
            {
                using var connection = Database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                new LibraryIdentityWriter(connection, transaction, Database.DatabaseType, new EditionSelector(Logger)).MergeBook(Target.Id, Source.Id);
                if (abort)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"History\"", transaction: transaction), Is.EqualTo(Source.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\"", transaction: transaction), Is.EqualTo(Source.Id));
                    Assert.That(connection.QuerySingle<string>("SELECT \"BookIds\" FROM \"Blacklist\"", transaction: transaction), Is.EqualTo($"[{Source.Id}]"));
                    Assert.That(connection.QuerySingle<string>("SELECT \"AdditionalInfo\" FROM \"PendingReleases\"", transaction: transaction), Does.Contain($"[{Source.Id}]"));
                    throw new InvalidOperationException("abort after transfer");
                }
                transaction.Commit();
            }
        }

        private sealed class IdentityEvents : IHandle<LibraryIdentityChangedEvent>, IHandle<BookDeletedEvent>, IHandle<BookInfoRefreshedEvent>
        {
            public readonly List<LibraryIdentityChangedEvent> Changed = new();
            public readonly List<BookDeletedEvent> Deleted = new();
            public readonly List<BookInfoRefreshedEvent> Refreshed = new();
            public void Handle(BookInfoRefreshedEvent message) => Refreshed.Add(message);
            public void Handle(LibraryIdentityChangedEvent message) => Changed.Add(message);
            public void Handle(BookDeletedEvent message) => Deleted.Add(message);
            public IEventAggregator Aggregator() => new EventAggregator(Logger, InterfaceProxy.Proxy<IServiceFactory>((method, args) =>
            {
                var type = method.GetGenericArguments().Single();
                var matches = type.IsInstanceOfType(this);
                var handlers = Array.CreateInstance(type, matches ? 1 : 0);
                if (matches) handlers.SetValue(this, 0);
                return handlers;
            }));
        }

        private static RefreshAuthorService Refresh(IMainDatabase database, Logger logger = null, IAuthorSyncMetadataService sync = null, IdentityEvents events = null)
        {
            var books = new BookRepository(database, null);
            var editions = new EditionRepository(database, null);
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name == nameof(IBookService.GetBooksByAuthorId)
                ? books.GetBooksByAuthorId((int)args[0]) : throw new InvalidOperationException(method.Name));
            var editionService = InterfaceProxy.Proxy<IEditionService>((method, args) => method.Name == nameof(IEditionService.GetEditionsByBook)
                ? args[0] is int bookId ? editions.FindByBook(new[] { bookId }) : editions.FindByBook((IEnumerable<int>)args[0]) : throw new InvalidOperationException(method.Name));
            var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                ? new BasicRepository<BookFile>(database, null).All().ToList() : throw new InvalidOperationException(method.Name));
            return new RefreshAuthorService(MetadataHealthGateStub.Ready(), null, null, bookService, editionService, null, null, null, (events ?? new IdentityEvents()).Aggregator(), null, media,
                new global::NzbDrone.Core.History.HistoryRepository(database, (events ?? new IdentityEvents()).Aggregator()), null, null, null, null, null, sync, null, null, logger ?? Logger, mainDatabase: database, bookRepository: new BookRepository(database, (events ?? new IdentityEvents()).Aggregator()), authorRepository: new AuthorRepository(database, (events ?? new IdentityEvents()).Aggregator(), logger ?? Logger));
        }

        [Test]
        public void published_pocket_change_updates_both_copies_without_moving_files_or_history(
            [Values(false, true)] bool postgres, [Values(false, true)] bool sameEdition, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, sameEdition);
                var books = new BookRepository(database, null);
                library.Source.MediaType = library.Target.MediaType = mediaType;
                books.UpdateMany(new[] { library.Source, library.Target });
                library.SourceEdition.ReadingFormatId = library.TargetEdition.ReadingFormatId = mediaType == BookMediaType.Ebook ? 3 : 2;
                new EditionRepository(database, null).UpdateMany(new[] { library.SourceEdition, library.TargetEdition });
                var files = new BasicRepository<BookFile>(database, null);
                files.Insert(new BookFile { EditionId = library.TargetEdition.Id, Path = "/fixture/second-copy", Size = 9876,
                    Quality = new(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                var opposite = books.Insert(new Book { AuthorId = library.Source.AuthorId, Title = "Dune", CleanTitle = "dune", TitleSlug = "opposite-copy",
                    MediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    GoodreadsWorkId = "gr:3634639", AnyEditionOk = true, Added = DateTime.UtcNow });
                var editionRepository = new EditionRepository(database, null);
                editionRepository.Insert(new Edition { BookId = opposite.Id, Title = "Dune", ForeignEditionId = library.SourceEdition.ForeignEditionId });
                using var connection = database.OpenConnection();
                var beforeFiles = connection.Query<string>("SELECT \"Id\" || ':' || \"EditionId\" || ':' || \"Path\" FROM \"BookFiles\" ORDER BY \"Id\"").ToArray();
                var beforeHistory = connection.Query<string>("SELECT \"BookId\" || ':' || \"EditionId\" || ':' || \"DownloadId\" FROM \"History\" ORDER BY \"Id\"").ToArray();
                var events = new IdentityEvents();
                var refresh = Refresh(database, events: events);
                var published = new Book { Title = "Dune", CleanTitle = "dune", TitleSlug = "server-canonical-dune", MediaType = mediaType, GoodreadsWorkId = "gr:9993634639",
                    RemoteProviderIds = new() { "gr:9993634639" }, Editions = new() { library.SourceEdition, library.TargetEdition } };
                for (var pass = 0; pass < 2; pass++) Assert.That(refresh.RepairOverMergedBookEditions(library.Source.AuthorId, new() { published }), Is.Empty);
                Assert.Multiple(() =>
                {
                    Assert.That(events.Changed.Count, Is.EqualTo(1), "one committed identity batch, then no rewrite on repeated refresh");
                    Assert.That(books.Get(library.Source.Id).GoodreadsWorkId, Is.EqualTo(published.GoodreadsWorkId));
                    Assert.That(books.Get(library.Target.Id).GoodreadsWorkId, Is.EqualTo(published.GoodreadsWorkId));
                    Assert.That(books.Get(library.Source.Id).TitleSlug, Is.EqualTo(library.Source.TitleSlug));
                    Assert.That(books.Get(library.Target.Id).TitleSlug, Is.EqualTo(library.Target.TitleSlug));
                    Assert.That(books.Get(opposite.Id).GoodreadsWorkId, Is.EqualTo("gr:3634639"));
                    Assert.That(connection.Query<string>("SELECT \"Id\" || ':' || \"EditionId\" || ':' || \"Path\" FROM \"BookFiles\" ORDER BY \"Id\""), Is.EqualTo(beforeFiles));
                    Assert.That(connection.Query<string>("SELECT \"BookId\" || ':' || \"EditionId\" || ':' || \"DownloadId\" FROM \"History\" ORDER BY \"Id\""), Is.EqualTo(beforeHistory));
                    Assert.That(new ProviderAliasService(new ProviderAliasRepository(database, null)).FindBookIds("work", new[] { published.GoodreadsWorkId }), Is.EquivalentTo(new[] { library.Source.Id, library.Target.Id }));
                });
            });
        }

        [Test]
        public void same_served_pocket_keeps_both_populated_local_copies([Values(false, true)] bool postgres, [Values(false, true)] bool sameEdition)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, sameEdition);
                var second = new BasicRepository<BookFile>(database, null).Insert(new BookFile { EditionId = library.TargetEdition.Id,
                    Path = "/library/Frank Herbert/Dune/Second recording.m4b", Size = 9876, Quality = new QualityModel(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                Assert.Throws<InvalidOperationException>(() => library.Merge());
                using var connection = database.OpenConnection();
                Assert.Multiple(() =>
                {
                    Assert.That(connection.Query<int>("SELECT \"Id\" FROM \"Books\" ORDER BY \"Id\""), Is.EqualTo(new[] { library.Source.Id, library.Target.Id }));
                    Assert.That(connection.Query<int>("SELECT \"EditionId\" FROM \"BookFiles\" ORDER BY \"Id\""), Is.EqualTo(new[] { library.SourceEdition.Id, library.TargetEdition.Id }));
                    Assert.That(connection.Query<string>("SELECT \"Path\" FROM \"BookFiles\" ORDER BY \"Id\""), Is.EqualTo(new[] { library.File.Path, second.Path }));
                    Assert.That(connection.Query<int>("SELECT \"BookId\" FROM \"History\""), Is.EqualTo(new[] { library.Source.Id }));
                });
            });
        }

        [Test]
        public void refresh_absorbs_only_the_empty_duplicate_preserving_populated_row_settings([Values(false, true)] bool postgres, [Values(false, true)] bool sameEdition)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, sameEdition);
                var books = new BookRepository(database, null);
                library.Source.GoodreadsWorkId = "gr:111";
                library.Source.RemoteProviderIds = new() { "gr:111" };
                library.Source.WantedNarratorId = 27;
                library.Source.LastSearchTime = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
                library.Source.AddOptions = new AddBookOptions { SearchForNewBook = false };
                books.Update(library.Source);
                var published = new Book { Title = "Dune", CleanTitle = "dune", GoodreadsWorkId = library.Target.GoodreadsWorkId,
                    RemoteProviderIds = new() { library.Target.GoodreadsWorkId }, Editions = new() { library.SourceEdition, library.TargetEdition } };
                Assert.That(Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, new() { published }), Is.EqualTo(new[] { library.Target.Id }));
                using var connection = database.OpenConnection();
                var saved = books.Get(library.Source.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(books.Find(library.Target.Id), Is.Null);
                    Assert.That(saved.GoodreadsWorkId, Is.EqualTo(published.GoodreadsWorkId));
                    Assert.That(saved.WantedNarratorId, Is.EqualTo(27));
                    Assert.That(saved.LastSearchTime, Is.EqualTo(library.Source.LastSearchTime));
                    Assert.That(saved.AddOptions.SearchForNewBook, Is.False);
                    Assert.That(saved.TitleSlug, Is.EqualTo(library.Source.TitleSlug));
                    Assert.That(saved.AudiobookMonitored, Is.True);
                    Assert.That(connection.QuerySingle<int>("SELECT \"EditionId\" FROM \"BookFiles\""), Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(connection.QuerySingle<string>("SELECT \"Path\" FROM \"BookFiles\""), Is.EqualTo(library.File.Path));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"History\""), Is.EqualTo(library.Source.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\""), Is.EqualTo(library.Source.Id));
                    Assert.That(new ProviderAliasService(new ProviderAliasRepository(database, null)).FindBookIds("work", new[] { "gr:111" }), Is.Empty);
                });
            });
        }

        [Test]
        public void late_failure_rolls_back_files_editions_books_and_grab_state([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                using (var seed = database.OpenConnection())
                {
                    seed.Execute("UPDATE \"History\" SET \"BookId\" = @Id, \"EditionId\" = @edition; UPDATE \"DownloadHistory\" SET \"BookId\" = @Id; UPDATE \"Blacklist\" SET \"BookIds\" = @ids; UPDATE \"PendingReleases\" SET \"AdditionalInfo\" = @pending;",
                        new { library.Target.Id, edition = library.TargetEdition.Id, ids = $"[{library.Target.Id}]", pending = $"{{\"bookIds\":[{library.Target.Id}]}}" });
                }
                Assert.That(() => library.Merge(true), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("abort after transfer"));
                using var connection = database.OpenConnection();
                Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"Editions\" WHERE \"Id\" = @Id", new { library.TargetEdition.Id }), Is.EqualTo(library.Target.Id));
                Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\""), Is.EqualTo(library.Target.Id));
                Assert.That(new BookRepository(database, null).Find(library.Source.Id), Is.Not.Null);
                Assert.That(new BookRepository(database, null).Find(library.Target.Id), Is.Not.Null);
                Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).Path, Is.EqualTo(library.File.Path));
                Assert.That(connection.QuerySingle<string>("SELECT \"BookIds\" FROM \"Blacklist\""), Is.EqualTo($"[{library.Target.Id}]"));
                Assert.That(connection.QuerySingle<string>("SELECT \"AdditionalInfo\" FROM \"PendingReleases\""), Does.Contain($"[{library.Target.Id}]"));
            });
        }

        [Test]
        public void cross_media_and_intentional_copies_cannot_be_collapsed([Values(false, true)] bool postgres, [Values(false, true)] bool manualCopy)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, true);
                using var connection = database.OpenConnection();
                if (manualCopy) connection.Execute("UPDATE \"Books\" SET \"UnitKeyHash\" = 'copy' WHERE \"Id\" = @Id", new { library.Target.Id });
                else connection.Execute("UPDATE \"Books\" SET \"MediaType\" = 1 WHERE \"Id\" = @Id", new { library.Target.Id });
                Assert.Throws<InvalidOperationException>(() => library.Merge());
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Books\""), Is.EqualTo(2));
            });
        }
        [Test]
        public void partial_edition_rehome_moves_only_edition_proven_history_and_pending_targets([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var editions = new BasicRepository<Edition>(database, null);
                var remaining = editions.Insert(new Edition { BookId = library.Source.Id, Title = "Dune", ForeignEditionId = "gr:789", GoodreadsEditionId = 789, Narrator = "Other Narrator" });
                using var connection = database.OpenConnection();
                connection.Execute("UPDATE \"Editions\" SET \"ManualAdd\" = @pin WHERE \"Id\" = @Id", new { pin = true, library.SourceEdition.Id });
                connection.Execute(@"INSERT INTO ""PendingReleases"" (""AuthorId"", ""Title"", ""Added"", ""Release"", ""Reason"", ""AdditionalInfo"")
                    VALUES (@AuthorId, 'book-only', @date, '{}', 0, @targets)", new { library.Source.AuthorId, date = DateTime.UtcNow, targets = $"{{\"bookIds\":[{library.Source.Id}]}}" });
                using (var transaction = connection.BeginTransaction())
                {
                    var removed = new LibraryIdentityWriter(connection, transaction, database.DatabaseType, new EditionSelector(Logger))
                        .MoveEditions(new[] { new LibraryIdentityWriter.EditionMove(library.SourceEdition.Id, library.Target.Id) });
                    Assert.That(removed, Is.Empty);
                    transaction.Commit();
                }
                Assert.Multiple(() =>
                {
                    Assert.That(editions.Get(library.SourceEdition.Id).BookId, Is.EqualTo(library.Target.Id));
                    Assert.That(editions.Get(library.SourceEdition.Id).ManualAdd, Is.True);
                    Assert.That(editions.Get(remaining.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"History\""), Is.EqualTo(library.Target.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\""), Is.EqualTo(library.Target.Id));
                    var pending = System.Text.Json.Nodes.JsonNode.Parse(connection.QuerySingle<string>("SELECT \"AdditionalInfo\" FROM \"PendingReleases\" WHERE \"Title\" = 'Dune'"));
                    Assert.That(pending["targetEditionIds"][library.Target.Id.ToString()].GetValue<int>(), Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(pending["bookIds"][0].GetValue<int>(), Is.EqualTo(library.Target.Id));
                    Assert.That(connection.QuerySingle<string>("SELECT \"AdditionalInfo\" FROM \"PendingReleases\" WHERE \"Title\" = 'book-only'"), Does.Contain($"[{library.Source.Id}]"));
                    Assert.That(connection.QuerySingle<string>("SELECT \"BookIds\" FROM \"Blacklist\""), Is.EqualTo($"[{library.Source.Id}]"));
                });
            });
        }

        [Test]
        public void pocket_repair_preserves_the_populated_pinned_source_and_its_settings([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                library.Source.WantedNarratorId = 42;
                library.Source.AnyEditionOk = false;
                library.Source.LastSearchTime = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
                library.Target.GoodreadsWorkId = "gr:9993634639";
                library.Target.RemoteProviderIds = new() { "gr:9993634639" };
                books.UpdateMany(new[] { library.Source, library.Target });
                using var connection = database.OpenConnection();
                connection.Execute("UPDATE \"Editions\" SET \"ManualAdd\" = @pin WHERE \"Id\" = @Id", new { pin = true, library.SourceEdition.Id });
                var served = new Book
                {
                    Title = "Dune", CleanTitle = "dune", MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:9993634639",
                    RemoteProviderIds = new() { "gr:9993634639" },
                    Editions = new() { library.SourceEdition, library.TargetEdition }
                };
                var removed = Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, new() { served });
                Assert.That(removed, Is.EqualTo(new[] { library.Target.Id }));
                var survivor = books.Get(library.Source.Id);
                Assert.That(survivor.GoodreadsWorkId, Is.EqualTo(served.GoodreadsWorkId));
                Assert.That(survivor.WantedNarratorId, Is.EqualTo(42));
                Assert.That(survivor.AnyEditionOk, Is.False);
                Assert.That(survivor.LastSearchTime, Is.EqualTo(library.Source.LastSearchTime));
                Assert.That(books.Find(library.Target.Id), Is.Null);
                var edition = new BasicRepository<Edition>(database, null).Get(library.SourceEdition.Id);
                Assert.That(edition.BookId, Is.EqualTo(library.Source.Id));
                Assert.That(edition.ManualAdd, Is.True);
                Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(edition.Id));
            });
        }

        [Test]
        public void identity_remap_projects_conversion_cleanup_flags_in_the_same_write([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var jobs = new ConversionJobRepository(database, null);
                var job = jobs.Insert(new ConversionJob
                {
                    DownloadId = "cleanup-remap", Status = ConversionJobStatus.Cancelled,
                    RequestJson = System.Text.Json.JsonSerializer.Serialize(new ConversionJobRequest
                    {
                        BookId = library.Target.Id, DiscardRequested = true, CleanupError = "file is locked"
                    }),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                using var connection = database.OpenConnection();
                connection.Execute(@"UPDATE ""ConversionJobs"" SET ""CleanupPending"" = @value, ""CleanupAttention"" = @value WHERE ""Id"" = @Id",
                    new { value = false, job.Id });
                library.Merge();
                var saved = jobs.Get(job.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(System.Text.Json.JsonSerializer.Deserialize<ConversionJobRequest>(saved.RequestJson).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(saved.CleanupPending, Is.True);
                    Assert.That(saved.CleanupAttention, Is.True);
                });
            });
        }

        [Test]
        public void in_flight_conversion_state_saves_do_not_restore_the_deleted_book_target(
            [Values(false, true)] bool postgres, [Values("MarkReady", "Requeue", "MarkCancelled")] string transition)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var jobs = new ConversionJobRepository(database, null);
                var job = jobs.Insert(new ConversionJob
                {
                    DownloadId = "preserved-torrent", Status = ConversionJobStatus.Converting,
                    RequestJson = System.Text.Json.JsonSerializer.Serialize(new ConversionJobRequest { BookId = library.Target.Id }),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                library.Merge();
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), jobs, null, null, null, null, Logger);
                var method = typeof(ConversionJobService).GetMethod(transition, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                method.Invoke(service, transition == "MarkReady"
                    ? new object[] { job, "finished", System.Threading.CancellationToken.None }
                    : new object[] { job, "state changed" });
                var saved = jobs.Get(job.Id);
                Assert.That(System.Text.Json.JsonSerializer.Deserialize<ConversionJobRequest>(saved.RequestJson).BookId, Is.EqualTo(library.Source.Id));
                Assert.That(saved.Status, Is.EqualTo(transition == "MarkReady" ? ConversionJobStatus.ReadyToImport :
                    transition == "Requeue" ? ConversionJobStatus.Queued : ConversionJobStatus.Cancelled));
            });
        }

    }
}
