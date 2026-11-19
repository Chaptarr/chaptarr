using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    public partial class ProviderAliasReplacementFixture
    {
        [Test]
        public void malformed_snapshot_is_rejected_atomically_on_both_engines([Values(false, true)] bool postgres,
            [Values("broken", ":123", "gr:")] string token)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = Seed(database);
                using var connection = database.OpenConnection();
                var before = connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" ORDER BY \"Id\"").ToArray();
                Assert.That(() => connection.Execute("UPDATE \"Authors\" SET \"ProviderIds\" = @snapshot WHERE \"Id\" = @Id",
                    new { snapshot = System.Text.Json.JsonSerializer.Serialize(new { author = new[] { token } }), rows.Author.Id }), Throws.Exception.With.Message.Contains("Invalid provider identity token"));
                Assert.That(connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" ORDER BY \"Id\""), Is.EqualTo(before));
                connection.Execute("UPDATE \"Authors\" SET \"ProviderIds\" = @snapshot WHERE \"Id\" = @Id",
                    new { snapshot = "{\"author\":[\"gr:123\"]}", rows.Author.Id });
                Assert.That(new ProviderAliasService(new ProviderAliasRepository(database, null)).FindAuthorIds(new[] { "gr:123" }), Is.EqualTo(new[] { rows.Author.Id }));
            });
        }

        [Test]
        public void freshly_served_equal_snapshot_keeps_migration_json_and_projection_rows([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = Seed(database).Author;
                using var connection = database.OpenConnection();
                const string spacedJson = "{\"author\": [\"gr:456\", \"gr:123\"]}";
                connection.Execute("UPDATE \"Authors\" SET \"ProviderIds\" = @json WHERE \"Id\" = @Id", new { json = spacedJson, author.Id });
                var before = connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" ORDER BY \"Id\"").ToArray();
                var repository = new BasicRepository<Author>(database, null);
                var loaded = repository.Get(author.Id);
                loaded.RemoteProviderIds = new() { "gr:123", "gr:456" };
                repository.Update(loaded);
                Assert.That(repository.Get(author.Id).ProviderIds, Is.EqualTo(spacedJson));
                Assert.That(connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" ORDER BY \"Id\""), Is.EqualTo(before));
            });
        }

        [Test]
        public void existing_author_metadata_save_projects_new_scalars_and_withdraws_old_ids_immediately([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = Seed(database).Author;
                var repository = new BasicRepository<Author>(database, null);
                var writes = 0;
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name != nameof(IAuthorService.UpdateAuthor)) throw new AssertionException(method.Name);
                    writes++;
                    return repository.Update((Author)args[0]);
                });
                var library = new AuthorLibraryService(authorService, null, null, null, null, null, null, null, null, null, null,
                    null, null, database, null, null, null, LogManager.GetCurrentClassLogger());
                var remote = new Author { Name = author.Name, CleanName = author.CleanName, SortName = author.SortName, TitleSlug = author.TitleSlug, HardcoverAuthorId = "hc:99", RemoteProviderIds = new() { "hc:99", "hc:100" } };
                typeof(AuthorLibraryService).GetMethod("ApplyAuthorMetadata", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(library, new object[] { repository.Get(author.Id), remote });
                Assert.That(writes, Is.EqualTo(1));
                Assert.That(repository.Get(author.Id).HardcoverAuthorId, Is.EqualTo("hc:99"));
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, null));
                Assert.That(aliases.GetAliases("Author", author.Id, "author").Select(id => $"{id.Provider}:{id.NormalizedProviderId}"), Is.EquivalentTo(new[] { "hc:99", "hc:100" }));
            });
        }
    }
}
