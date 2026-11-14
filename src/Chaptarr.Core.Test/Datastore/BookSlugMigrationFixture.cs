using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookSlugMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void migration_preserves_rows_and_reserves_existing_suffixes_on_each_media_side(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repo = new BookRepository(database, InterfaceProxy.Proxy<IEventAggregator>((m, a) => null));
                var author = new AuthorRepository(database, InterfaceProxy.Proxy<IEventAggregator>((m, a) => null), LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = "Fixture author", CleanName = "fixtureauthor", SortName = "fixtureauthor", SortNameLastFirst = "authorfixture",
                    TitleSlug = "fixture-author", Path = "/library/fixture", Added = DateTime.UtcNow
                });
                Book Add(string slug, BookMediaType media, bool monitored = false) => repo.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Fixture", CleanTitle = "fixture", TitleSlug = slug, MediaType = media,
                    AudiobookMonitored = media == BookMediaType.Audiobook && monitored,
                    EbookMonitored = media == BookMediaType.Ebook && monitored,
                    Added = DateTime.UtcNow
                });
                var first = Add("résumé", BookMediaType.Audiobook);
                var selected = Add("RÉSUMÉ", BookMediaType.Audiobook, true);
                var suffix = Add("résumé_2", BookMediaType.Audiobook);
                var third = Add("résumé", BookMediaType.Audiobook);
                var legacy = Add("résumé-2", BookMediaType.Audiobook);
                var ebook = Add("RÉSUMÉ", BookMediaType.Ebook);
                using var connection = database.OpenConnection();
                var migrationConnection = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(
                    migrationConnection, new MigrationContext(MigrationType.Main, 127),
                    postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Upgrade();
                Assert.Multiple(() =>
                {
                    Assert.That(repo.Get(selected.Id).TitleSlug, Is.EqualTo("résumé"));
                    Assert.That(repo.Get(first.Id).TitleSlug, Is.EqualTo("résumé_3"));
                    Assert.That(repo.Get(third.Id).TitleSlug, Is.EqualTo("résumé_4"));
                    Assert.That(repo.Get(suffix.Id).TitleSlug, Is.EqualTo("résumé_2"));
                    Assert.That(repo.Get(ebook.Id).TitleSlug, Is.EqualTo("résumé"));
                    Assert.That(repo.Get(legacy.Id).TitleSlug, Is.EqualTo("résumé-2"), "Existing links are never reformatted");
                    Assert.That(repo.Count(), Is.EqualTo(6));
                    Assert.That(repo.FindBySlug("RÉSUMÉ").Select(book => book.Id), Is.EquivalentTo(new[] { selected.Id, ebook.Id }));
                });
                Upgrade();
                Assert.That(repo.Get(first.Id).TitleSlug, Is.EqualTo("résumé_3"));
                Assert.That(() => Add("résumé", BookMediaType.Audiobook), Throws.Exception);
            }, desiredVersion: 126);
        }
    }
}
