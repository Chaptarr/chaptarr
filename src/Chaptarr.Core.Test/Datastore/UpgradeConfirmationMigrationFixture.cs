using System;
using System.Text.Json;
using System.Collections.Generic;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Messaging.Events;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Api.V1.Profiles.Quality;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class UpgradeConfirmationMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void existing_profiles_default_to_replace_and_explicit_values_survive_migration_replay(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""QualityProfiles"" (""Name"", ""ProfileType"", ""Cutoff"", ""Items"", ""UpgradeAllowed"", ""MinFormatScore"", ""CutoffFormatScore"", ""FormatItems"") VALUES ('existing', 1, 0, '[]', @Enabled, 0, 0, '[]')", new { Enabled = true });
                var migrationConnection = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(
                    migrationConnection, new MigrationContext(MigrationType.Main, 124), database.DatabaseType);
                Upgrade();
                Assert.That(connection.QuerySingle<bool>("SELECT \"ConfirmAutomaticUpgrades\" FROM \"QualityProfiles\" WHERE \"Name\"='existing'"), Is.False);
                connection.Execute("UPDATE \"QualityProfiles\" SET \"ConfirmAutomaticUpgrades\"=@Value WHERE \"Name\"='existing'", new { Value = true });
                new MediaFileRepository(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null)).Insert(new BookFile
                {
                    Path = "/library/standard.m4b", EditionId = 0, AudioProductionOverride = false,
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel()
                });
                Upgrade();
                Assert.That(connection.QuerySingle<bool>("SELECT \"ConfirmAutomaticUpgrades\" FROM \"QualityProfiles\" WHERE \"Name\"='existing'"), Is.True);
                Assert.That(connection.QuerySingle<bool?>("SELECT \"AudioProductionOverride\" FROM \"BookFiles\" WHERE \"Path\"='/library/standard.m4b'"), Is.False);
            }, desiredVersion: 123);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profile_api_roundtrip_preserves_upgrade_choice(bool wait)
        {
            var profile = new QualityProfile { ProfileType = ProfileType.Audiobook, UpgradeAllowed = true, ConfirmAutomaticUpgrades = wait, Items = new List<QualityProfileQualityItem>() };
            Assert.That(profile.ToResource().ToModel().ConfirmAutomaticUpgrades, Is.EqualTo(wait));
            profile.UpgradeAllowed = false;
            Assert.That(profile.ToResource().ToModel().ConfirmAutomaticUpgrades, Is.False);
        }

        [TestCase("{}", false, null)]
        [TestCase("{\"audioProductionOverride\":null}", true, null)]
        [TestCase("{\"audioProductionOverride\":false}", true, false)]
        [TestCase("{\"audioProductionOverride\":true}", true, true)]
        public void recording_override_distinguishes_omitted_from_clear_and_false(string json, bool specified, bool? expected)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var single = JsonSerializer.Deserialize<BookFileResource>(json, options);
            var bulk = JsonSerializer.Deserialize<BookFileListResource>(json, options);
            var manual = JsonSerializer.Deserialize<ManualImportFile>(json, options);
            Assert.Multiple(() =>
            {
                Assert.That(single.AudioProductionOverrideSpecified, Is.EqualTo(specified));
                Assert.That(bulk.AudioProductionOverrideSpecified, Is.EqualTo(specified));
                Assert.That(manual.AudioProductionOverrideSpecified, Is.EqualTo(specified));
                Assert.That(single.AudioProductionOverride, Is.EqualTo(expected));
                Assert.That(bulk.AudioProductionOverride, Is.EqualTo(expected));
                Assert.That(manual.AudioProductionOverride, Is.EqualTo(expected));
            });
        }
    }
}
