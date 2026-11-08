using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [NonParallelizable]
    public class ProviderIdentityProjectionMigrationFixture
    {
        [Test]
        public void upgrade_preserves_every_owned_alias_including_pruned_editions_until_refresh([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var author = connection.ExecuteScalar<int>(@"INSERT INTO ""Authors"" (""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"")
                    VALUES ('Fixture Author', 'fixture author', '/fixture', @on, @now) RETURNING ""Id""", new { on = true, now = DateTime.UtcNow });
                var book = connection.ExecuteScalar<int>(@"INSERT INTO ""Books"" (""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""Added"", ""AnyEditionOk"")
                    VALUES (@author, 'Ready Player One', 'ready player one', 0, @now, @on) RETURNING ""Id""", new { author, on = true, now = DateTime.UtcNow });
                connection.Execute(@"INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                    VALUES ('Book', @book, 'work', 'hc', '26363', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                           ('Book', @book, 'edition', 'hc', '30402322', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                           ('Book', 999999, 'work', 'gr', '999999', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)", new { book });
                var before = connection.Query<string>(@"SELECT ""Scope"" || ':' || ""Provider"" || ':' || ""NormalizedProviderId""
                    FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = @book ORDER BY 1", new { book }).ToArray();
                new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connection.ConnectionString,
                    new MigrationContext(MigrationType.Main, 126), database.DatabaseType);
                Assert.That(connection.Query<string>(@"SELECT ""Scope"" || ':' || ""Provider"" || ':' || ""NormalizedProviderId""
                    FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = @book ORDER BY 1", new { book }), Is.EqualTo(before));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"ProviderIds\" FROM \"Books\" WHERE \"Id\" = @book", new { book }), Does.Contain("30402322"));
                Assert.That(connection.ExecuteScalar<int>("SELECT count(*) FROM \"ProviderAliasIndex\" WHERE \"EntityId\" = 999999"), Is.Zero);
                connection.Execute("UPDATE \"Books\" SET \"ProviderIds\" = @ids WHERE \"Id\" = @book", new { book, ids = "{\"work\":[\"hc:30391354\"]}" });
                Assert.That(connection.Query<string>("SELECT \"NormalizedProviderId\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\" = 'Book' AND \"EntityId\" = @book", new { book }), Is.EqualTo(new[] { "30391354" }));
            }, desiredVersion: 125);
        }
    }
}
