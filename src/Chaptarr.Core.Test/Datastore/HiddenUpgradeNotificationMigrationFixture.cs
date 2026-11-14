using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Discord;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class HiddenUpgradeNotificationMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void upgrade_preserves_independent_notification_choices(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new NotificationRepository(database, InterfaceProxy.Proxy<IEventAggregator>((method, args) => null));
                var definitions = (from import in new[] { false, true }
                                   from upgrade in new[] { false, true }
                                   select repository.Insert(new NotificationDefinition
                                   {
                                       Name = $"Import={import}, upgrade={upgrade}",
                                       Implementation = "Discord",
                                       ConfigContract = "DiscordSettings",
                                       Settings = new DiscordSettings(),
                                       Tags = new HashSet<int> { 3 },
                                       OnReleaseImport = import,
                                       OnUpgrade = upgrade,
                                       OnGrab = true
                                   })).ToList();
                using var connection = database.OpenConnection();
                var migrationConnection = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(
                    migrationConnection, new MigrationContext(MigrationType.Main, 128),
                    postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Upgrade();
                foreach (var original in definitions)
                {
                    var saved = repository.Get(original.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.OnUpgrade, Is.EqualTo(original.OnUpgrade));
                        Assert.That(saved.OnReleaseImport, Is.EqualTo(original.OnReleaseImport));
                        Assert.That(saved.OnGrab, Is.True);
                        Assert.That(saved.Name, Is.EqualTo(original.Name));
                        Assert.That(saved.Tags, Is.EquivalentTo(original.Tags));
                    });
                }
                // Once the independent option is visible, an explicit upgrade-only choice survives startup.
                var selected = definitions.Single(x => !x.OnReleaseImport && x.OnUpgrade);
                connection.Execute(@"UPDATE ""Notifications"" SET ""OnUpgrade""=@Value WHERE ""Id""=@Id", new { Value = true, selected.Id });
                Upgrade();
                Assert.That(repository.Get(selected.Id).OnUpgrade, Is.True);
                selected.Id = 0;
                Assert.That(repository.Get(repository.Insert(selected).Id).OnUpgrade, Is.True);
            }, desiredVersion: 120);
        }
    }
}
