using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    public partial class RefreshBookServiceMergeEditionSelectionFixture
    {
        [Test]
        public void one_failed_merge_does_not_stop_other_pairs_and_refresh_is_not_acknowledged([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var other = books.Insert(new Book { AuthorId = library.Source.AuthorId, Title = "Dune", CleanTitle = "dune", TitleSlug = "other-empty", Added = DateTime.UtcNow, AnyEditionOk = true });
                library.Target.Editions = new() { library.TargetEdition };
                library.Target.BookFiles = new();
                other.Editions = new();
                other.BookFiles = new();
                using var connection = database.OpenConnection();
                connection.Execute(postgres
                    ? $"CREATE FUNCTION abort_one_merge() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD.\"Id\" = {library.Target.Id} THEN RAISE EXCEPTION 'one pair failed'; END IF; RETURN OLD; END; $$; CREATE TRIGGER abort_one_merge BEFORE DELETE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION abort_one_merge();"
                    : $"CREATE TRIGGER abort_one_merge BEFORE DELETE ON \"Books\" WHEN OLD.\"Id\" = {library.Target.Id} BEGIN SELECT RAISE(ABORT, 'one pair failed'); END;");
                var syncRows = new AuthorSyncMetadataRepository(database, null);
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = library.Source.AuthorId, ExternalAuthorId = "hc:123", ETag = "new-but-not-applied" });
                var sync = new AuthorSyncMetadataService(syncRows, NLog.LogManager.GetCurrentClassLogger());
                using var factory = new NLog.LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                var config = new NLog.Config.LoggingConfiguration();
                config.AddRuleForAllLevels(sink);
                factory.Configuration = config;
                var refresh = Refresh(database, factory.GetLogger("merge-followup"), sync);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var context = typeof(RefreshAuthorService).GetMethod("CreateContext", flags).Invoke(refresh, null);
                var sorted = new RefreshEntityServiceBase<Author, Book>.SortedChildren
                { Merged = new() { Tuple.Create(library.Target, library.Source), Tuple.Create(other, library.Source) } };
                typeof(RefreshAuthorService).GetMethod("RefreshChildren", flags)
                    .Invoke(refresh, new object[] { sorted, new List<Book>(), new Author(), false, false, null, context });
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => typeof(RefreshAuthorService).GetMethod("ValidateChildrenRefresh", flags)
                    .Invoke(refresh, new object[] { new Author { Id = library.Source.AuthorId }, context }));
                var checkpoint = syncRows.FindByAuthorId(library.Source.AuthorId);
                Assert.Multiple(() =>
                {
                    Assert.That(error.InnerException, Is.TypeOf<AuthorIdentityUnverifiedException>());
                    Assert.That(error.InnerException.InnerException, Is.TypeOf<AggregateException>());
                    Assert.That(books.Find(library.Target.Id), Is.Not.Null, "failed pair rolled back");
                    Assert.That(new BasicRepository<Edition>(database, null).Get(library.TargetEdition.Id).BookId, Is.EqualTo(library.Target.Id));
                    Assert.That(error.InnerException.ToString(), Does.Contain("pair failed"));
                    Assert.That(books.Find(other.Id), Is.Null, "later pair completed");
                    Assert.That(checkpoint.SyncFailureCount, Is.EqualTo(1), "one canonical failure write");
                    Assert.That(checkpoint.NextSyncNotBefore, Is.InRange(DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61)));
                    Assert.That(checkpoint.ETag, Is.Null, "next refresh must not skip the failed ownership change");
                    Assert.That(checkpoint.LastSyncStatus, Is.EqualTo(SyncStatus.Failed));
                    Assert.That(string.Join("\n", sink.Logs), Does.Contain($"Could not merge book {library.Target.Id} into {library.Source.Id}"));
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                });
            });
        }

        [Test]
        public void refresh_moves_filed_edition_to_its_served_pocket_with_history_and_selection(
            [Values(false, true)] bool postgres, [Values(false, true)] bool equivalent, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, equivalent);
                var books = new BookRepository(database, null);
                var editions = new BasicRepository<Edition>(database, null);
                library.Source.MediaType = library.Target.MediaType = media;
                library.SourceEdition.ReadingFormatId = library.TargetEdition.ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3;
                editions.UpdateMany(new[] { library.SourceEdition, library.TargetEdition });
                library.Target.GoodreadsWorkId = "gr:222";
                library.Target.RemoteProviderIds = new() { "gr:222" };
                books.UpdateMany(new List<Book> { library.Source, library.Target });
                var retained = editions.Insert(new Edition { BookId = library.Source.Id, Title = "Dune", ForeignEditionId = "gr:888", GoodreadsEditionId = 888 });
                var files = new BasicRepository<BookFile>(database, null);
                foreach (var part in new[] { 1, 2 }) files.Insert(new BookFile { EditionId = retained.Id, Path = $"/library/Dune/{part}.mp3",
                    Size = 100, Quality = new QualityModel(), MediaInfo = new(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                var remote = new List<Book>
                {
                    new() { Title = "Dune", MediaType = media, GoodreadsWorkId = library.Source.GoodreadsWorkId, Editions = new() { retained } },
                    new() { Title = "Dune Messiah", MediaType = media, GoodreadsWorkId = "gr:222", Editions = new() { library.SourceEdition, library.TargetEdition } }
                };
                var beforeId = library.SourceEdition.Id;
                var survivingId = equivalent ? library.TargetEdition.Id : beforeId;
                Assert.That(Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, remote), Is.Empty);
                using var connection = database.OpenConnection();
                Assert.Multiple(() =>
                {
                    Assert.That(files.Get(library.File.Id).EditionId, Is.EqualTo(survivingId));
                    Assert.That(files.Get(library.File.Id).Path, Is.EqualTo(library.File.Path));
                    Assert.That(editions.Get(survivingId).BookId, Is.EqualTo(library.Target.Id));
                    Assert.That(editions.Get(survivingId).Monitored, Is.True);
                    Assert.That(editions.Get(retained.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(books.Find(library.Source.Id), Is.Not.Null);
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"History\""), Is.EqualTo(library.Target.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"EditionId\" FROM \"History\""), Is.EqualTo(survivingId));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\""), Is.EqualTo(library.Target.Id));
                    Assert.That(connection.Query<int>("SELECT e.\"BookId\" FROM \"ProviderAliasIndex\" p JOIN \"Editions\" e ON e.\"Id\" = p.\"EntityId\" WHERE p.\"EntityType\" = 'Edition' AND p.\"Provider\" = 'gr' AND p.\"NormalizedProviderId\" = '44767458'"), Is.EqualTo(new[] { library.Target.Id }));
                });
            });
        }

        [Test]
        public void move_preserves_the_populated_origin_and_absorbs_only_the_empty_target([Values(false, true)] bool postgres, [Values(false, true)] bool equivalent)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, equivalent);
                var books = new BookRepository(database, null);
                library.Source.WantedNarratorId = 27;
                library.Source.LastSearchTime = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
                library.Source.AddOptions = new AddBookOptions { SearchForNewBook = false };
                library.Target.GoodreadsWorkId = "gr:999";
                library.Target.RemoteProviderIds = new() { "gr:999" };
                books.UpdateMany(new[] { library.Source, library.Target });
                var events = new IdentityEvents();
                using (var seed = database.OpenConnection())
                {
                    seed.Execute("UPDATE \"History\" SET \"BookId\" = @Id, \"EditionId\" = @edition; UPDATE \"DownloadHistory\" SET \"BookId\" = @Id; UPDATE \"Blacklist\" SET \"BookIds\" = @ids; UPDATE \"PendingReleases\" SET \"AdditionalInfo\" = @pending;",
                        new { library.Target.Id, edition = library.TargetEdition.Id, ids = $"[{library.Target.Id}]", pending = $"{{\"bookIds\":[{library.Target.Id}]}}" });
                }
                var served = RefreshEntityCopy.CloneBook(library.Target, false);
                served.Editions = new() { library.SourceEdition, library.TargetEdition };
                var removed = Refresh(database, events: events).RepairOverMergedBookEditions(library.Source.AuthorId, new() { served });
                var saved = books.Get(library.Source.Id);
                using var connection = database.OpenConnection();
                Assert.Multiple(() =>
                {
                    Assert.That(removed, Is.EqualTo(new[] { library.Target.Id }));
                    Assert.That(books.Find(library.Target.Id), Is.Null);
                    Assert.That(saved.GoodreadsWorkId, Is.EqualTo("gr:999"));
                    Assert.That(saved.WantedNarratorId, Is.EqualTo(27));
                    Assert.That(saved.LastSearchTime, Is.EqualTo(library.Source.LastSearchTime));
                    Assert.That(saved.AddOptions.SearchForNewBook, Is.False);
                    Assert.That(saved.TitleSlug, Is.EqualTo(library.Source.TitleSlug));
                    Assert.That(saved.AudiobookMonitored, Is.True);
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(new BasicRepository<Edition>(database, null).Get(library.SourceEdition.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"History\""), Is.EqualTo(library.Source.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"DownloadHistory\""), Is.EqualTo(library.Source.Id));
                    Assert.That(connection.QuerySingle<string>("SELECT \"BookIds\" FROM \"Blacklist\""), Is.EqualTo($"[{library.Source.Id}]"));
                    Assert.That(connection.QuerySingle<int>("SELECT \"IndexerId\" FROM \"DownloadHistory\""), Is.EqualTo(7));
                    Assert.That(connection.QuerySingle<string>("SELECT \"AdditionalInfo\" FROM \"PendingReleases\""), Does.Contain($"[{library.Source.Id}]"));
                    Assert.That(new BasicRepository<Edition>(database, null).Get(library.SourceEdition.Id).Narrator, Is.EqualTo("Scott Brick"));
                    Assert.That(events.Changed, Has.Count.EqualTo(1));
                    Assert.That(events.Changed.Single().RemovedBookIds, Is.EquivalentTo(removed));
                    Assert.That(events.Deleted, Is.Empty);
                });
            });
        }

        [Test]
        public void identity_adoption_and_ownership_changes_roll_back_together([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var original = books.Get(library.Source.Id).ProviderIds;
                library.Source.GoodreadsWorkId = "gr:999";
                library.Source.RemoteProviderIds = new() { "gr:999" };
                using var connection = database.OpenConnection();
                connection.Execute(postgres
                    ? "CREATE FUNCTION abort_identity_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'late delete failure'; END; $$; CREATE TRIGGER abort_identity_delete BEFORE DELETE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION abort_identity_delete();"
                    : "CREATE TRIGGER abort_identity_delete BEFORE DELETE ON \"Books\" BEGIN SELECT RAISE(ABORT, 'late delete failure'); END;");
                Assert.That(() => Refresh(database).ApplyEditionOwnershipMoves(library.Source.AuthorId,
                    new[] { new LibraryIdentityWriter.EditionMove(library.TargetEdition.Id, library.Source.Id) },
                    new[] { library.Source.Id }, new[] { library.Source }, new Dictionary<int, int>()), Throws.Exception.With.Message.Contains("late delete failure"));
                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(library.Source.Id).ProviderIds, Is.EqualTo(original));
                    Assert.That(books.Find(library.Target.Id), Is.Not.Null);
                    Assert.That(new BasicRepository<Edition>(database, null).Get(library.TargetEdition.Id).BookId, Is.EqualTo(library.Target.Id));
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(new ProviderAliasService(new ProviderAliasRepository(database, null)).FindBookIds("work", new[] { "gr:999" }), Is.Empty);
                });
            });
        }

        [Test]
        public void repeated_merge_or_missing_target_warns_and_leaves_remaining_rows_unchanged([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var writer = new LibraryIdentityWriter(connection, transaction, database.DatabaseType, new EditionSelector(Logger));
                var previous = NLog.LogManager.Configuration;
                var config = new NLog.Config.LoggingConfiguration();
                var warnings = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                config.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Warn, warnings, "*LibraryIdentityWriter");
                NLog.LogManager.Configuration = config;
                try
                {
                    Assert.That(writer.MergeBook(library.Target.Id, int.MaxValue), Is.False);
                    Assert.That(writer.MergeBook(library.Target.Id, library.Source.Id), Is.True);
                    Assert.That(writer.MergeBook(library.Target.Id, library.Source.Id), Is.False);
                    Assert.That(warnings.Logs, Has.Count.EqualTo(2));
                    Assert.That(warnings.Logs[0], Does.Contain("[BOOK-MERGE] Skipped").And.Contain("target book row is already missing"));
                    Assert.That(warnings.Logs[1], Does.Contain("[BOOK-MERGE] Skipped").And.Contain("source book row is already missing"));
                }
                finally { NLog.LogManager.Configuration = previous; }
                transaction.Commit();
                Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
            });
        }
    }
}
