using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [NonParallelizable]
    public partial class ProviderAliasReplacementFixture
    {
        private static IEventAggregator Events() => Proxy<IEventAggregator>((_, _) => null);

        private static (Author Author, Book Book, Edition Edition) Seed(IMainDatabase database)
        {
            var author = new Author { Name = "Identity Author", CleanName = "identityauthor", SortName = "identity author", Path = "/library/identity", Added = DateTime.UtcNow, TitleSlug = Guid.NewGuid().ToString(), RemoteProviderIds = new() { "gr:123", "gr:456" } };
            new AuthorRepository(database, Events(), LogManager.GetCurrentClassLogger()).Insert(author);
            var book = new Book { AuthorId = author.Id, Title = "Identity Book", CleanTitle = "identity book", TitleSlug = Guid.NewGuid().ToString(), RemoteProviderIds = new() { "gr:123", "gr:456" }, MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow, AnyEditionOk = true };
            new BookRepository(database, Events()).Insert(book);
            var edition = new Edition { BookId = book.Id, Title = book.Title, TitleSlug = Guid.NewGuid().ToString(), RemoteProviderIds = new() { "gr:123", "gr:456" } };
            new EditionRepository(database, Events()).Insert(edition);
            return (author, book, edition);
        }

        [Test]
        public void legacy_book_union_is_retained_but_never_proves_a_specific_edition([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = Seed(database);
                using var connection = database.OpenConnection();
                connection.Execute("UPDATE \"Books\" SET \"ProviderIds\" = @json WHERE \"Id\" = @Id",
                    new { rows.Book.Id, json = "{\"work\":[\"gr:123\"],\"edition\":[\"gr:999999\"]}" });
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, Events()));
                Assert.That(aliases.GetAliases("Book", rows.Book.Id, "edition"), Has.Count.EqualTo(1), "preserve legacy snapshot until authoritative refresh");
                Assert.That(aliases.FindBookIds("edition", new[] { "gr:999999" }), Is.Empty, "a union cannot identify a retained recording");
                Assert.That(aliases.FindBookIds("work", new[] { "gr:123" }), Is.EqualTo(new[] { rows.Book.Id }));
                rows.Edition.RemoteProviderIds = new() { "gr:999999" };
                new EditionRepository(database, Events()).Update(rows.Edition);
                Assert.That(aliases.FindBookIds("edition", new[] { "gr:999999" }), Is.EqualTo(new[] { rows.Book.Id }));
                var book = new BookRepository(database, Events()).Get(rows.Book.Id);
                book.RemoteProviderIds = new() { "gr:123" };
                new BookRepository(database, Events()).Update(book);
                Assert.That(aliases.GetAliases("Book", book.Id, "edition"), Is.Empty, "successful authoritative replacement retires the historical scope");
            });
        }

        [Test]
        public void snapshot_is_authority_and_settings_saves_do_not_restore_withdrawn_ids(
            [Values(false, true)] bool postgres, [Values("Author", "Book", "Edition")] string entityType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = Seed(database);
                var service = new ProviderAliasService(new ProviderAliasRepository(database, Events()));
                var scope = entityType == "Author" ? "author" : entityType == "Book" ? "work" : "edition";
                void Check<T>(T row) where T : ProviderIdentityEntity<T>, new()
                {
                    var repository = new BasicRepository<T>(database, Events());
                    var staleSettings = repository.Get(row.Id);
                    var fresh = repository.Get(row.Id);
                    Assert.That(fresh.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:123", "gr:456" }));
                    fresh.RemoteProviderIds = new() { "gr:789" };
                    repository.Update(fresh);
                    repository.Update(staleSettings);
                    Assert.That(service.GetAliases(entityType, row.Id, scope).Select(a => a.NormalizedProviderId), Is.EqualTo(new[] { "789" }));
                    fresh.RemoteProviderIds = new();
                    repository.Update(fresh);
                    Assert.That(service.GetAliases(entityType, row.Id, scope), Is.Empty);
                    Assert.That(repository.Get(row.Id).RemoteProviderIds, Is.Empty);
                    fresh.RemoteProviderIds = Enumerable.Range(1000, 100).Select(id => $"gr:{id}").ToHashSet();
                    repository.Update(fresh);
                    Assert.That(repository.Get(row.Id).RemoteProviderIds, Is.EquivalentTo(fresh.RemoteProviderIds));
                    Assert.That(service.GetAliases(entityType, row.Id, scope), Has.Count.EqualTo(100));
                }
                if (entityType == "Author") Check(rows.Author);
                else if (entityType == "Book") Check(rows.Book);
                else Check(rows.Edition);
            });
        }

        [Test]
        public void competing_refreshes_leave_one_complete_snapshot_not_a_union([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = Seed(database);
                using var start = new System.Threading.Barrier(2);
                var tasks = new[] { "gr:801", "gr:802" }.Select(id => System.Threading.Tasks.Task.Run(() =>
                {
                    var repository = new AuthorRepository(database, Events(), LogManager.GetCurrentClassLogger());
                    var author = repository.Get(rows.Author.Id);
                    author.RemoteProviderIds = new() { id };
                    start.SignalAndWait(TimeSpan.FromSeconds(10));
                    repository.Update(author);
                })).ToArray();
                System.Threading.Tasks.Task.WaitAll(tasks);
                var stored = new AuthorRepository(database, Events(), LogManager.GetCurrentClassLogger()).Get(rows.Author.Id);
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, Events())).GetAliases("Author", stored.Id, "author");
                Assert.That(aliases.Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Is.EqualTo(stored.RemoteProviderIds));
                Assert.That(aliases, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void row_and_projection_rollback_together_then_same_model_can_retry([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var row = Seed(database).Edition;
                var repository = new EditionRepository(database, Events());
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, Events()));
                row.RemoteProviderIds = new() { "gr:789" };
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    repository.UpdateMany(new[] { row }, connection, transaction);
                    Assert.That(connection.Query<string>("SELECT \"NormalizedProviderId\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\" = 'Edition'", transaction: transaction), Is.EqualTo(new[] { "789" }));
                    transaction.Rollback();
                }
                Assert.That(aliases.GetAliases("Edition", row.Id, "edition"), Has.Count.EqualTo(2));
                repository.Update(row);
                Assert.That(aliases.GetAliases("Edition", row.Id, "edition").Single().NormalizedProviderId, Is.EqualTo("789"));
                repository.Delete(row.Id);
                Assert.That(aliases.GetAliases("Edition", row.Id, "edition"), Is.Empty);
            });
        }
    }
}
