using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    public partial class RefreshBookServiceMergeEditionSelectionFixture
    {
        [Test]
        public void shared_served_work_alias_does_not_repeat_identity_repair(
            [Values(false, true)] bool postgres, [Values(false, true)] bool reverse,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new Library(database, false);
                var authors = new AuthorRepository(database, null, Logger);
                var books = new BookRepository(database, null);
                var editions = new EditionRepository(database, null);
                books.DeleteManyAtomic(new[] { library.Target.Id });
                library.Source.MediaType = media;
                library.Source.BaseBookId = library.Source.GoodreadsWorkId = "gr:111";
                library.Source.RemoteProviderIds = new() { "gr:111", "hc:999" };
                books.Update(library.Source);
                library.SourceEdition.ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3;
                editions.Update(library.SourceEdition);
                var author = authors.Get(library.Source.AuthorId);
                author.Overview = "";
                author.Path = TestContext.CurrentContext.TestDirectory;
                authors.Update(author);
                Book Pocket(string id, string title, Edition edition) => new()
                {
                    AuthorId = author.Id, Title = title, CleanTitle = title, TitleSlug = id.Replace(':', '-'),
                    BaseBookId = id, GoodreadsWorkId = id, RemoteProviderIds = new() { id, "hc:999" },
                    AnyEditionOk = true, MediaType = media, Editions = new() { edition }, Added = DateTime.UtcNow
                };
                var served = authors.Get(author.Id);
                served.Books = new()
                {
                    Pocket("gr:111", "Dune Messiah", new Edition { Title = "Dune Messiah", ForeignEditionId = "gr:999", GoodreadsEditionId = 999, ReadingFormatId = library.SourceEdition.ReadingFormatId }),
                    Pocket("gr:222", "Dune", RefreshEntityCopy.CloneEdition(library.SourceEdition))
                };
                if (reverse) served.Books.Reverse();
                var events = new IdentityEvents();
                var refresh = CreateCatalogRefresh(database, events);
                refresh.ReconcileAuthorBlob(authors.Get(author.Id), served);
                var firstEvents = events.Changed.Count;
                Assert.That(firstEvents, Is.GreaterThan(0), "the first pass really repairs identity");
                using var connection = database.OpenConnection();
                connection.Execute("CREATE TABLE identity_writes (value INTEGER);");
                connection.Execute(postgres
                    ? "CREATE FUNCTION count_book_identity_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO identity_writes VALUES (1); RETURN NEW; END $$; CREATE TRIGGER count_book_identity_write AFTER UPDATE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION count_book_identity_write();"
                    : "CREATE TRIGGER count_book_identity_write AFTER UPDATE ON \"Books\" BEGIN INSERT INTO identity_writes VALUES (1); END;");
                refresh.RepairOverMergedBookEditions(author.Id, served.Books);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM identity_writes"), Is.Zero, "unchanged repair must issue no book write, even a no-op UPDATE");
                refresh.ReconcileAuthorBlob(authors.Get(author.Id), served);
                Assert.Multiple(() =>
                {
                    Assert.That(events.Changed.Count, Is.EqualTo(firstEvents));
                    Assert.That(books.All(), Has.Count.EqualTo(2));
                    Assert.That(books.Get(library.Source.Id).GoodreadsWorkId, Is.EqualTo("gr:222"));
                    Assert.That(editions.Get(library.SourceEdition.Id).BookId, Is.EqualTo(library.Source.Id));
                    Assert.That(new MediaFileRepository(database, null).Get(library.File.Id).EditionId, Is.EqualTo(library.SourceEdition.Id));
                    Assert.That(events.Deleted, Is.Empty);
                });
            });
        }
    }
}
