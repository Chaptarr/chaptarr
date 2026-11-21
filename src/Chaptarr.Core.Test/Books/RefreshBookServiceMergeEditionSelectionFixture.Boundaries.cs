using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    public partial class RefreshBookServiceMergeEditionSelectionFixture
    {
        [Test]
        public void fileless_row_needed_by_another_served_pocket_is_not_absorbed([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                library.Source.GoodreadsWorkId = "gr:111";
                library.Source.RemoteProviderIds = new() { "gr:111" };
                library.Target.RemoteProviderIds = new() { library.Target.GoodreadsWorkId, "gr:222" };
                books.UpdateMany(new[] { library.Source, library.Target });
                var remote = new List<Book>
                {
                    new() { Title = "Dune", CleanTitle = "dune", GoodreadsWorkId = library.Target.GoodreadsWorkId, Editions = new() { library.SourceEdition } },
                    new() { Title = "Dune Messiah", CleanTitle = "dunemessiah", GoodreadsWorkId = "gr:222", Editions = new() { library.TargetEdition } }
                };
                Assert.That(Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, remote), Is.Empty);
                Assert.That(books.All().Select(book => book.Id), Is.EquivalentTo(new[] { library.Source.Id, library.Target.Id }));
                Assert.That(books.Get(library.Source.Id).GoodreadsWorkId, Is.EqualTo(library.Target.GoodreadsWorkId));
                Assert.That(new BasicRepository<Edition>(database, null).Get(library.TargetEdition.Id).BookId, Is.EqualTo(library.Target.Id));
            });
        }

        [Test]
        public void incomplete_move_plan_rolls_back_without_dangling_files([Values(false, true)] bool postgres,
            [Values("source", "target", "equivalent", "merge-edition")] string missing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                using var connection = database.OpenConnection();
                using (var transaction = connection.BeginTransaction())
                {
                    var writer = new LibraryIdentityWriter(connection, transaction, database.DatabaseType, new EditionSelector(Logger));
                    Action execute = missing == "merge-edition" ? () => writer.MergeEdition(library.SourceEdition.Id, int.MaxValue)
                        : () => writer.MoveEditions(new[] { new LibraryIdentityWriter.EditionMove(missing == "source" ? int.MaxValue : library.SourceEdition.Id,
                            missing == "target" ? int.MaxValue : library.Target.Id, missing == "equivalent" ? int.MaxValue : null) });
                    Assert.That(execute, Throws.TypeOf<InvalidOperationException>().With.Message.Contains("missing"));
                }
                Assert.That(new BasicRepository<BookFile>(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                Assert.That(connection.Query<int>("SELECT \"Id\" FROM \"Books\" ORDER BY \"Id\""), Is.EqualTo(new[] { library.Source.Id, library.Target.Id }));
                Assert.That(new BasicRepository<Edition>(database, null).Get(library.SourceEdition.Id).BookId, Is.EqualTo(library.Source.Id));
            });
        }

        [Test]
        public void populated_target_is_kept_as_a_separate_copy_during_identity_adoption([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, true);
                var files = new BasicRepository<BookFile>(database, null);
                var other = files.Insert(new BookFile { EditionId = library.TargetEdition.Id, Path = "/library/copy2.m4b", Size = 100,
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new(), MediaInfo = new() });
                var books = new BookRepository(database, null);
                library.Target.GoodreadsWorkId = "gr:999";
                library.Target.RemoteProviderIds = new() { "gr:999" };
                books.Update(library.Target);
                using var connection = database.OpenConnection();
                var served = RefreshEntityCopy.CloneBook(library.Target, false);
                served.Editions = new() { library.SourceEdition, library.TargetEdition };
                Assert.That(Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, new() { served }), Is.Empty);
                Assert.Multiple(() =>
                {
                    Assert.That(books.All().Count(), Is.EqualTo(2));
                    Assert.That(books.Get(library.Source.Id).GoodreadsWorkId, Is.EqualTo("gr:999"));
                    Assert.That(files.Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(files.Get(other.Id).EditionId, Is.EqualTo(library.TargetEdition.Id));
                });
            });
        }

        [Test]
        public void repair_does_not_absorb_a_fileless_destination_that_receives_another_recording([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var third = books.Insert(new Book { AuthorId = library.Source.AuthorId, Title = "Dune collection",
                    CleanTitle = "dunecollection", TitleSlug = "dune-collection", AnyEditionOk = true, Added = DateTime.UtcNow });
                var editions = new EditionRepository(database, null);
                var moving = editions.Insert(new Edition { BookId = third.Id, Title = "Dune", ForeignEditionId = "gr:222", ReadingFormatId = 2 });
                var staying = editions.Insert(new Edition { BookId = third.Id, Title = "Dune Messiah", ForeignEditionId = "gr:333", ReadingFormatId = 2 });
                var files = new BasicRepository<BookFile>(database, null);
                var otherFile = files.Insert(new BookFile { EditionId = moving.Id, Path = "/library/other-copy.m4b", Size = 100,
                    Quality = new(), MediaInfo = new(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                using var connection = database.OpenConnection();
                // The third row holds two served pockets. Keep its primary recording,
                // and send the other recording to the initially fileless destination.
                for (var i = 0; i < 2; i++) files.Insert(new BookFile { EditionId = staying.Id, Path = $"/library/remaining-{i}.mp3", Size = 100,
                    Quality = new(), MediaInfo = new(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                third.GoodreadsWorkId = "gr:999";
                third.RemoteProviderIds = new() { "gr:999" };
                books.Update(third);
                var published = new List<Book>
                {
                    new() { Title = "Dune", GoodreadsWorkId = library.Target.GoodreadsWorkId, Editions = new() { library.SourceEdition, library.TargetEdition, moving } },
                    new() { Title = "Dune Messiah", GoodreadsWorkId = "gr:999", Editions = new() { staying } }
                };
                Assert.That(Refresh(database).RepairOverMergedBookEditions(library.Source.AuthorId, published), Is.Empty);
                Assert.Multiple(() =>
                {
                    Assert.That(books.All().Count(), Is.EqualTo(3));
                    Assert.That(editions.Get(library.SourceEdition.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(editions.Get(moving.Id).BookId, Is.EqualTo(library.Target.Id));
                    Assert.That(editions.Get(staying.Id).BookId, Is.EqualTo(third.Id));
                    Assert.That(files.Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(files.Get(otherFile.Id).EditionId, Is.EqualTo(moving.Id));
                });
            });
        }

        [Test]
        public void low_level_move_refuses_to_empty_a_populated_origin([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var writer = new LibraryIdentityWriter(connection, transaction, database.DatabaseType, new EditionSelector(Logger));
                Assert.That(() => writer.MoveEditions(new[] { new LibraryIdentityWriter.EditionMove(library.SourceEdition.Id, library.Target.Id) }),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Cannot empty populated book"));
                Assert.That(connection.QuerySingle<int>("SELECT \"BookId\" FROM \"Editions\" WHERE \"Id\" = @Id", new { library.SourceEdition.Id }, transaction), Is.EqualTo(library.Source.Id));
                Assert.That(connection.QuerySingle<int>("SELECT \"EditionId\" FROM \"BookFiles\"", transaction: transaction), Is.EqualTo(library.SourceEdition.Id));
            });
        }

        [Test]
        public void fileless_merge_batch_reads_acquisition_targets_once_per_author([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var books = new BookRepository(database, null);
                var extras = Enumerable.Range(1, 20).Select(i => new Book { AuthorId = library.Source.AuthorId, Title = "Duplicate", TitleSlug = "duplicate-" + i,
                    CleanTitle = "duplicate", AnyEditionOk = true, Added = DateTime.UtcNow }).ToList();
                books.InsertMany(extras);
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var writer = new LibraryIdentityWriter(connection, transaction, database.DatabaseType, new EditionSelector(Logger));
                foreach (var duplicate in extras) Assert.That(writer.MergeBook(duplicate.Id, library.Source.Id), Is.True);
                Assert.That(writer.TargetReadCount, Is.EqualTo(3), "one Blacklist read, one PendingReleases read and one active ConversionJobs read for all twenty merges");
                transaction.Commit();
            });
        }
    }
}
