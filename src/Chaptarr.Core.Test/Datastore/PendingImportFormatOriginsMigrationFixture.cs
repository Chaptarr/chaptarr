using System;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class PendingImportFormatOriginsMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void existing_requested_sides_keep_their_origin_and_replay_preserves_manual_override(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""PendingAuthorImport"" (""ProviderId"", ""ProviderPrefix"", ""AudiobookStatus"", ""EbookStatus"", ""OverallStatus"", ""CreatedAt"", ""NextAttemptAt"", ""RequestedBy"")
                    VALUES ('gr:58', 'gr', 1, 0, 1, @Now, @Now, 'ImportList:Favorites')", new { Now = DateTime.UtcNow });
                var connectionString = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connectionString,
                    new MigrationContext(MigrationType.Main, 125), database.DatabaseType);
                Upgrade();
                var row = connection.QuerySingle<PendingAuthorImport>("SELECT * FROM \"PendingAuthorImport\"");
                Assert.That(row.AudiobookRequestedBy, Is.EqualTo("ImportList:Favorites"));
                Assert.That(row.EbookRequestedBy, Is.Null);
                Assert.That(row.ExplicitBookSearches, Is.Null);
                var permission = new string(' ', 1024) + "[]";
                connection.Execute("UPDATE \"PendingAuthorImport\" SET \"ExplicitBookSearches\" = @permission", new { permission });
                Assert.That(connection.QuerySingle<string>("SELECT \"ExplicitBookSearches\" FROM \"PendingAuthorImport\""), Is.EqualTo(permission));
                connection.Execute("UPDATE \"PendingAuthorImport\" SET \"AudiobookRequestedBy\" = 'UserInterface'");
                Upgrade();
                Assert.That(connection.QuerySingle<string>("SELECT \"AudiobookRequestedBy\" FROM \"PendingAuthorImport\""), Is.EqualTo("UserInterface"));
            }, desiredVersion: 124);
        }
    }
}
