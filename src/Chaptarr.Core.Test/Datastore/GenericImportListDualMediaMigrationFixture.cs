using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class GenericImportListDualMediaMigrationFixture
    {
        [TestCase(false, "audio")]
        [TestCase(true, "audio")]
        [TestCase(false, "ebook")]
        [TestCase(true, "ebook")]
        [TestCase(false, "mixed")]
        [TestCase(true, "mixed")]
        [TestCase(false, "missing")]
        [TestCase(true, "missing")]
        [TestCase(false, "partial")]
        [TestCase(true, "partial")]
        [TestCase(false, "nearest")]
        [TestCase(true, "nearest")]
        [TestCase(false, "legacy")]
        [TestCase(true, "legacy")]
        [TestCase(false, "nullable")]
        [TestCase(true, "nullable")]
        [TestCase(false, "specific-null")]
        [TestCase(true, "specific-null")]
        [TestCase(false, "unknown-quality")]
        [TestCase(true, "unknown-quality")]
        [TestCase(false, "malformed")]
        [TestCase(true, "malformed")]
        [TestCase(false, "malformed-tags")]
        [TestCase(true, "malformed-tags")]
        public void public_upgrade_should_preserve_generic_lists_and_convert_only_their_configured_media(bool postgres, string scenario)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var path = @"C:\ListLibrary".AsOsAgnostic();
                var root = NewRoot(path, profiles);
                switch (scenario)
                {
                    case "audio": root.EbookSettings = null; root.FolderType = FolderType.Audiobook; break;
                    case "ebook": root.AudiobookSettings = null; root.FolderType = FolderType.Ebook; break;
                    case "missing": break;
                    case "partial":
                        root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = profiles.Audio, Monitored = false });
                        break;
                    case "nearest":
                        InsertRoot(connection, root);
                        path = Path.Combine(path, "Chosen");
                        root.Path = path;
                        root.AudiobookSettings = null;
                        root.FolderType = FolderType.Ebook;
                        break;
                    case "legacy":
                        root.AudiobookSettings = JsonConvert.SerializeObject(new { qualityProfileId = profiles.Audio, metadataProfileId = profiles.AudioMetadata, monitorExisting = 0, monitorFuture = false });
                        root.EbookSettings = JsonConvert.SerializeObject(new { qualityProfileId = profiles.Ebook, metadataProfileId = profiles.EbookMetadata, monitorFuture = true });
                        break;
                    case "nullable":
                    case "specific-null":
                        root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = profiles.Audio, MetadataProfileId = profiles.AudioMetadata, Monitored = null });
                        root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = profiles.Ebook, MetadataProfileId = profiles.EbookMetadata, Monitored = null });
                        break;
                }
                InsertRoot(connection, root);
                var destination = scenario == "missing" ? @"C:\MissingLibrary".AsOsAgnostic()
                    : scenario == "nearest" ? Path.Combine(path, "Authors") : path;
                var audio = scenario is not ("ebook" or "missing" or "partial" or "nearest");
                var ebook = scenario is not ("audio" or "missing");
                var mode = scenario == "specific-null" ? ImportListMonitorType.SpecificBook : ImportListMonitorType.None;
                var globalQuality = scenario == "unknown-quality" ? int.MaxValue : scenario == "ebook" ? profiles.LegacyEbook : profiles.LegacyAudio;
                var globalMetadata = scenario == "ebook" ? profiles.GeneralMetadata : profiles.LegacyAudioMetadata;
                var rows = new List<(int Id, string Implementation, JObject Source, Dictionary<string, object> Saved)>();
                foreach (var implementation in new[] { "ChaptarrImport", "LazyLibrarianImport" })
                {
                    var source = SourceSettings(implementation);
                    var id = InsertList(connection, implementation, destination, globalQuality, globalMetadata,
                        scenario == "malformed" ? "{broken" : source.ToString(Formatting.None), mode);
                    if (scenario == "malformed-tags")
                    {
                        connection.Execute(@"UPDATE ""ImportLists"" SET ""Tags""=@Tags WHERE ""Id""=@Id", new { Id = id, Tags = "never-log-this-source-value" });
                    }
                    rows.Add((id, implementation, source, SavedColumns(connection, id)));
                }
                var previousLogging = LogManager.Configuration;
                var warnings = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, warnings);
                try
                {
                    LogManager.Configuration = logging;
                    upgrade();
                }
                finally
                {
                    LogManager.Configuration = previousLogging;
                }
                foreach (var row in rows)
                {
                    var settings = ReadSettings(connection, row.Id);
                    Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.EqualTo(audio));
                    Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.EqualTo(ebook));
                    Assert.That(settings["audiobookRootFolderPath"]?.Value<string>(), Is.EqualTo(audio ? destination : null));
                    Assert.That(settings["ebookRootFolderPath"]?.Value<string>(), Is.EqualTo(ebook ? destination : null));
                    Assert.That(settings["audiobookQualityProfileId"]?.Value<int>(), Is.EqualTo(audio ? scenario == "unknown-quality" ? profiles.Audio : globalQuality : 0));
                    Assert.That(settings["ebookQualityProfileId"]?.Value<int>(), Is.EqualTo(ebook ? scenario == "ebook" ? globalQuality : profiles.Ebook : 0));
                    Assert.That(settings["audiobookMetadataProfileId"]?.Value<int>(), Is.EqualTo(audio ? globalMetadata : 0));
                    Assert.That(settings["ebookMetadataProfileId"]?.Value<int>(), Is.EqualTo(ebook ? scenario == "ebook" ? globalMetadata : profiles.EbookMetadata : 0));
                    Assert.That(settings["audiobookTags"]?.Values<int>(), Is.EquivalentTo(audio && scenario != "malformed-tags" ? new[] { 3, 5 } : Array.Empty<int>()));
                    Assert.That(settings["ebookTags"]?.Values<int>(), Is.EquivalentTo(ebook && scenario != "malformed-tags" ? new[] { 3, 5 } : Array.Empty<int>()));
                    if (scenario != "malformed")
                    {
                        foreach (var field in row.Source.Properties()) Assert.That(JToken.DeepEquals(settings[field.Name], field.Value), Is.True, field.Name);
                    }
                    else
                    {
                        Assert.That(warnings.Logs, Has.Some.Contains("Import list " + row.Id).And.Contains("invalid legacy settings"));
                    }
                    if (scenario == "malformed-tags")
                    {
                        Assert.That(warnings.Logs, Has.Some.Contains("Import list " + row.Id).And.Contains("invalid legacy destination tags"));
                    }
                    Assert.That(warnings.Logs, Has.None.Contains("never-log-this-source-value").And.None.Contains("synthetic-key"));
                    Assert.That(SavedColumns(connection, row.Id), Is.EquivalentTo(row.Saved));
                    var options = ReadOptions(connection, row.Id);
                    Assert.That(options.AudiobookMonitored, Is.EqualTo(mode == ImportListMonitorType.SpecificBook ? true : audio ? root.GetAudiobookSettings()?.Monitored : null));
                    Assert.That(options.EbookMonitored, Is.EqualTo(mode == ImportListMonitorType.SpecificBook ? true : ebook ? root.GetEbookSettings()?.Monitored : null));
                    Assert.That(options.AudiobookMonitor, Is.EqualTo(MonitorTypes.None));
                    Assert.That(options.EbookMonitor, Is.EqualTo(MonitorTypes.None));
                    Assert.That(options.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(options.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(options.AddMissingAuthors, Is.True);
                    Assert.That(options.ResumePausedAuthors, Is.False, "An upgrade must not authorize lists to resume paused authors");
                    Assert.That(connection.ExecuteScalar<bool>(@"SELECT ""ShouldMonitorExisting"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { row.Id }), Is.EqualTo(mode != ImportListMonitorType.None));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void public_upgrade_should_leave_existing_dual_settings_and_monitoring_conversion_unchanged(bool postgres)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var root = NewRoot(@"C:\DualLibrary".AsOsAgnostic(), profiles);
                InsertRoot(connection, root);
                var rows = new List<(int Id, string Settings)>();
                foreach (var implementation in new[] { "GoodreadsBookshelf", "GoodreadsListImportList", "GoodreadsSeriesImportList", "HardcoverLibraryImportList" })
                {
                    var json = "{ \"monitorAudiobooks\": false, \"monitorEbooks\": true, \"audiobookRootFolderPath\": " +
                        JsonConvert.SerializeObject(root.Path) + ", \"ebookRootFolderPath\": " + JsonConvert.SerializeObject(root.Path) +
                        ", \"audiobookTags\": [], \"ebookTags\": [8], \"sourceSetting\": \"keep\" }";
                    rows.Add((InsertList(connection, implementation, root.Path, profiles.LegacyAudio, profiles.GeneralMetadata, json, ImportListMonitorType.EntireAuthor), json));
                }
                connection.Execute(@"UPDATE ""ImportLists"" SET ""Tags""='[]'");
                upgrade();
                foreach (var row in rows)
                {
                    Assert.That(connection.ExecuteScalar<string>(@"SELECT ""Settings"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { row.Id }), Is.EqualTo(row.Settings));
                    var options = ReadOptions(connection, row.Id);
                    Assert.That(options.AudiobookMonitored, Is.False);
                    Assert.That(options.EbookMonitored, Is.True);
                    Assert.That(options.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                    Assert.That(options.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                    Assert.That(options.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(options.ResumePausedAuthors, Is.False);
                    Assert.That(connection.ExecuteScalar<bool>(@"SELECT ""ShouldMonitorExisting"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { row.Id }), Is.True);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void public_upgrade_should_fold_legacy_destination_tags_only_into_enabled_dual_sides(bool postgres)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var root = NewRoot(@"C:\TaggedLibrary".AsOsAgnostic(), profiles);
                InsertRoot(connection, root);
                var rows = new List<(int Id, bool Audio, bool Ebook, JObject Source)>();
                foreach (var implementation in new[] { "GoodreadsBookshelf", "GoodreadsListImportList", "GoodreadsSeriesImportList", "HardcoverLibraryImportList" })
                {
                    foreach (var (audio, ebook) in new[] { (true, false), (false, true), (true, true) })
                    {
                        var source = SourceSettings(implementation);
                        source["monitorAudiobooks"] = audio;
                        source["monitorEbooks"] = ebook;
                        source["audiobookRootFolderPath"] = root.Path;
                        source["ebookRootFolderPath"] = root.Path;
                        source["AudiobookTags"] = new JArray(8, 5);
                        source["ebookTags"] = new JArray(9);
                        source["audiobookQualityProfileId"] = profiles.Audio;
                        source["ebookQualityProfileId"] = profiles.Ebook;
                        var id = InsertList(connection, implementation, root.Path, profiles.LegacyAudio, profiles.GeneralMetadata, source.ToString(Formatting.None), ImportListMonitorType.EntireAuthor);
                        rows.Add((id, audio, ebook, source));
                    }
                }
                upgrade();
                foreach (var row in rows)
                {
                    var stored = ReadSettings(connection, row.Id);
                    Assert.That(stored["AudiobookTags"]?.Values<int>(), Is.EquivalentTo(row.Audio ? new[] { 3, 5, 8 } : new[] { 8, 5 }));
                    Assert.That(stored["audiobookTags"], Is.Null, "Case-insensitive lookup must update the saved property rather than duplicate it");
                    Assert.That(stored["ebookTags"]?.Values<int>(), Is.EquivalentTo(row.Ebook ? new[] { 3, 5, 9 } : new[] { 9 }));
                    foreach (var field in row.Source.Properties().Where(field => !field.Name.EndsWith("Tags", StringComparison.Ordinal)))
                    {
                        Assert.That(JToken.DeepEquals(stored[field.Name], field.Value), Is.True, field.Name);
                    }
                    Assert.That(connection.ExecuteScalar<string>(@"SELECT ""Tags"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { row.Id }), Is.EqualTo("[5,3,5]"));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void shared_conversion_should_preserve_explicit_generic_fields_and_options(bool postgres)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var root = NewRoot(@"C:\ExplicitLibrary".AsOsAgnostic(), profiles);
                InsertRoot(connection, root);
                var source = SourceSettings("ChaptarrImport");
                var explicitFields = new JObject
                {
                    ["monitorAudiobooks"] = false, ["monitorEbooks"] = true,
                    ["audiobookRootFolderPath"] = JValue.CreateNull(), ["ebookRootFolderPath"] = root.Path,
                    ["audiobookQualityProfileId"] = 0, ["ebookQualityProfileId"] = profiles.LegacyEbook,
                    ["audiobookMetadataProfileId"] = 0, ["ebookMetadataProfileId"] = profiles.GeneralMetadata,
                    ["audiobookTags"] = new JArray(), ["ebookTags"] = new JArray(9)
                };
                source.Merge(explicitFields);
                var id = InsertList(connection, "ChaptarrImport", root.Path, profiles.LegacyAudio, profiles.LegacyAudioMetadata,
                    source.ToString(Formatting.None), ImportListMonitorType.SpecificBook);
                upgrade();
                var options = "{\"addMissingAuthors\":false,\"resumePausedAuthors\":true,\"audiobookMonitored\":false,\"ebookMonitored\":null,\"audiobookMonitor\":null,\"ebookMonitor\":\"missing\",\"audiobookMonitorNewItems\":null,\"ebookMonitorNewItems\":\"none\"}";
                connection.Execute(@"UPDATE ""ImportLists"" SET ""MonitoringOptions""=@Options WHERE ""Id""=@Id", new { Options = options, Id = id });
                using var transaction = connection.BeginTransaction();
                Convert(connection, transaction);
                transaction.Commit();
                Assert.That(JToken.DeepEquals(ReadSettings(connection, id), source), Is.True);
                Assert.That(connection.ExecuteScalar<string>(@"SELECT ""MonitoringOptions"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { Id = id }), Is.EqualTo(options));
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 100)]
        [TestCase(true, 100)]
        public void conversion_should_load_one_compatible_snapshot_for_the_complete_batch(bool postgres, int count)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var root = NewRoot(@"C:\BatchLibrary".AsOsAgnostic(), profiles);
                InsertRoot(connection, root);
                for (var index = 0; index < count; index++)
                {
                    InsertList(connection, index % 2 == 0 ? "ChaptarrImport" : "LazyLibrarianImport", root.Path,
                        profiles.LegacyAudio, profiles.LegacyAudioMetadata, SourceSettings("ChaptarrImport").ToString(Formatting.None), ImportListMonitorType.None);
                }
                upgrade();
                connection.Execute(@"UPDATE ""ImportLists"" SET ""Settings""='{}'");
                var readers = new List<string>();
                var observed = CommandObserver.Wrap(connection, command => readers.Add(command.CommandText));
                var timer = System.Diagnostics.Stopwatch.StartNew();
                using (var transaction = connection.BeginTransaction())
                {
                    Convert(observed, transaction);
                    transaction.Commit();
                }
                Assert.That(readers, Has.Count.EqualTo(4));
                foreach (var table in new[] { "RootFolders", "QualityProfiles", "MetadataProfiles", "ImportLists" })
                {
                    Assert.That(readers.Count(sql => sql.Contains("\"" + table + "\"")), Is.EqualTo(1), table);
                }
                var stored = connection.Query<string>(@"SELECT ""Settings"" FROM ""ImportLists""").Select(JObject.Parse).ToList();
                Assert.That(stored, Has.Count.EqualTo(count));
                Assert.That(stored.All(settings => settings["monitorAudiobooks"]?.Value<bool>() == true && settings["monitorEbooks"]?.Value<bool>() == true), Is.True);
                TestContext.Progress.WriteLine($"Generic list migration {(postgres ? "PostgreSQL" : "SQLite")}: {count} lists, {readers.Count} snapshot readers, {timer.ElapsedMilliseconds} ms; existing transactional batch updates.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void migration_111_output_must_keep_its_reviewed_fingerprint(bool postgres)
        {
            WithLegacyDatabase(postgres, (connection, upgrade) =>
            {
                var profiles = SeedProfiles(connection);
                var path = @"C:\FingerprintRoot".AsOsAgnostic();
                InsertRoot(connection, NewRoot(path, profiles));
                foreach (var implementation in new[] { "ChaptarrImport", "LazyLibrarianImport", "GoodreadsBookshelf", "HardcoverLibraryImportList" })
                foreach (var mode in new[] { ImportListMonitorType.None, ImportListMonitorType.SpecificBook, ImportListMonitorType.EntireAuthor })
                foreach (var root in new[] { path, path + "/missing/nested", @"C:\Unavailable".AsOsAgnostic() })
                    InsertList(connection, implementation, root, profiles.LegacyAudio, profiles.GeneralMetadata,
                        SourceSettings(implementation).ToString(Formatting.None), mode);

                // Identity input intentionally covers foreign IDs, case, nested edition IDs and multiple ASINs.
                connection.Execute(@"INSERT INTO ""Authors"" (""Name"",""Path"",""Added"",""Monitored"",""CleanName"") VALUES ('Fingerprint Author', '/fingerprint', CURRENT_TIMESTAMP, @No, 'fingerprint author');
                    INSERT INTO ""Books"" (""AuthorId"",""Title"",""CleanTitle"",""AnyEditionOk"",""Added"")
                    VALUES (1, 'Fingerprint Book', 'fingerprint book', @No, CURRENT_TIMESTAMP);
                    INSERT INTO ""Editions"" (""BookId"",""Title"",""Monitored"",""ForeignEditionId"",""GoodreadsEditionId"",""HardcoverEditionId"",""OpenLibraryEditionId"",""GoogleBooksEditionId"",""Asin"",""AudibleASIN"",""Asins"")
                    VALUES (1, 'Fingerprint Edition', @No, 'hc:edition:456', 123, 'hc:456', 'OL789M', 'gb:xyz', 'b000000001', 'B000000002', '[""B000000003"",""az:B000000004""]');", new { No = false });
                upgrade();
                var rows = connection.Query(@"SELECT ""Settings"", ""MonitoringOptions"", ""ShouldMonitorExisting"" FROM ""ImportLists"" ORDER BY ""Id""");
                var output = new JArray(rows.Select(row => new JObject
                {
                    ["settings"] = JObject.Parse((string)row.Settings),
                    ["options"] = JObject.Parse((string)row.MonitoringOptions),
                    ["monitorExisting"] = System.Convert.ToBoolean(row.ShouldMonitorExisting)
                }));
                output.Add(JArray.FromObject(connection.Query(@"SELECT ""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId"" FROM ""ProviderAliasIndex"" ORDER BY ""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId""")));
                foreach (var value in output.Descendants().OfType<JValue>().Where(value => value.Type == JTokenType.String))
                    value.Value = ((string)value.Value).Replace('\\', '/').Replace("C:/", "/C/");
                var json = output.ToString(Formatting.None);
                var fingerprint = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
                TestContext.WriteLine("Migration 111 output: " + fingerprint);
                TestContext.WriteLine(json);
                Assert.That(fingerprint, Is.EqualTo("2001E5220D01756E8C7BEF6671F58BF1780BFDAF093BD1195ABCCD6120343078"),
                    "Migration 111 output changed. Preserve its historical contract; review live helper changes before upgrading existing databases.");
            });
        }

        private static void Convert(IDbConnection connection, IDbTransaction transaction)
        {
            var migration = new add_import_list_monitoring_options();
            var method = migration.GetType().GetMethod("ConvertImportListSettings", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            method.Invoke(migration, new object[] { connection, transaction });
        }

        private static RootFolder NewRoot(string path, Profiles profiles)
        {
            var root = new RootFolder { Path = path, FolderType = FolderType.Mixed };
            root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = profiles.Audio, MetadataProfileId = profiles.AudioMetadata, Monitored = false });
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = profiles.Ebook, MetadataProfileId = profiles.EbookMetadata, Monitored = true });
            return root;
        }

        private static void InsertRoot(IDbConnection connection, RootFolder root) =>
            connection.Execute(@"INSERT INTO ""RootFolders"" (""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"") VALUES (@Path,@FolderType,@AudiobookSettings,@EbookSettings)", root);

        private static JObject SourceSettings(string implementation) => JObject.FromObject(new
        {
            baseUrl = "http://source.invalid", apiKey = "synthetic-key",
            profileIds = new[] { 17 }, tagIds = new[] { 19 }, rootFolderPaths = new[] { "/source/root" },
            sourceOption = implementation
        });

        private static int InsertList(IDbConnection connection, string implementation, string path, int quality, int metadata, string settings, ImportListMonitorType mode) =>
            connection.QuerySingle<int>(@"INSERT INTO ""ImportLists"" (""Name"",""Implementation"",""ConfigContract"",""RootFolderPath"",""QualityProfileId"",""MetadataProfileId"",""Tags"",""Settings"",""ShouldMonitor"",""ShouldMonitorExisting"",""MonitorNewItems"",""EnableAutomaticAdd"",""ShouldSearch"")
                VALUES (@Name,@Implementation,@Contract,@Path,@Quality,@Metadata,'[5,3,5]',@Settings,@Mode,@Yes,@New,@No,@Yes) RETURNING ""Id""",
                new { Name = "Saved " + implementation, Implementation = implementation, Contract = implementation == "ChaptarrImport" ? "ChaptarrSettings" : implementation == "LazyLibrarianImport" ? "LazyLibrarianImportSettings" : implementation + "Settings",
                    Path = path, Quality = quality, Metadata = metadata, Settings = settings, Mode = (int)mode, Yes = true, No = false, New = (int)NewItemMonitorTypes.New });

        private static Dictionary<string, object> SavedColumns(IDbConnection connection, int id) =>
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT ""Name"",""Implementation"",""ConfigContract"",""RootFolderPath"",""QualityProfileId"",""MetadataProfileId"",""Tags"",""ShouldMonitor"",""MonitorNewItems"",""EnableAutomaticAdd"",""ShouldSearch"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { Id = id })).ToDictionary(pair => pair.Key, pair => pair.Value);

        private static JObject ReadSettings(IDbConnection connection, int id) =>
            JObject.Parse(connection.ExecuteScalar<string>(@"SELECT ""Settings"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { Id = id }));

        private static ImportListMonitoringOptions ReadOptions(IDbConnection connection, int id) =>
            JsonConvert.DeserializeObject<ImportListMonitoringOptions>(connection.ExecuteScalar<string>(@"SELECT ""MonitoringOptions"" FROM ""ImportLists"" WHERE ""Id""=@Id", new { Id = id }));

        private sealed record Profiles(int Audio, int Ebook, int LegacyAudio, int LegacyEbook, int AudioMetadata, int EbookMetadata, int GeneralMetadata, int LegacyAudioMetadata);

        private static Profiles SeedProfiles(IDbConnection connection)
        {
            int Quality(string name, int type) => connection.QuerySingle<int>(@"INSERT INTO ""QualityProfiles"" (""Name"",""ProfileType"",""Cutoff"",""Items"",""UpgradeAllowed"",""MinFormatScore"",""CutoffFormatScore"",""FormatItems"") VALUES (@Name,@Type,0,'[]',@No,0,0,'[]') RETURNING ""Id""", new { Name = name, Type = type, No = false });
            int Metadata(string name, int type) => connection.QuerySingle<int>(@"INSERT INTO ""MetadataProfiles"" (""Name"",""ProfileType"",""MinPages"",""MinPopularity"",""AllowedLanguages"") VALUES (@Name,@Type,0,0,'eng') RETURNING ""Id""", new { Name = name, Type = type });
            return new Profiles(Quality("Root audio", 1), Quality("Root ebook", 2), Quality("Saved audio override", 1), Quality("Saved ebook override", 2),
                Metadata("Root audio", 1), Metadata("Root ebook", 2), Metadata("Saved general override", 0), Metadata("Saved audio override", 1));
        }

        private static void WithLegacyDatabase(bool postgres, Action<IDbConnection, Action> test)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "generic_list_upgrade_" + Guid.NewGuid().ToString("N") + ".db");
            var databaseName = "generic_list_upgrade_" + Guid.NewGuid().ToString("N");
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(adminString)) Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING for PostgreSQL migration coverage");
            var connectionString = postgres ? new NpgsqlConnectionStringBuilder(adminString) { Database = databaseName, Pooling = false }.ToString()
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{databaseName}\"");
            }
            try
            {
                var controller = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                var type = postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite;
                controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 110), type);
                using IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                test(connection, () => controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 111), type));
            }
            finally
            {
                if (postgres)
                {
                    using var admin = new NpgsqlConnection(adminString);
                    admin.Open();
                    admin.Execute($"DROP DATABASE \"{databaseName}\"");
                }
                else if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
