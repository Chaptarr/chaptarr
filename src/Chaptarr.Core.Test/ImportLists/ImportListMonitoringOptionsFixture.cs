using System;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ClientSchema;
using DryIoc;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListMonitoringOptionsFixture
    {
        private sealed class OptionsList : ImportListBase<GoodreadsBookshelfImportListSettings>
        {
            public OptionsList() : base(null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger()) { }
            public override string Name => "Options";
            public override ImportListType ListType => ImportListType.Other;
            public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);
            public override IList<ImportListItemInfo> Fetch() => new List<ImportListItemInfo>();
            protected override void Test(List<FluentValidation.Results.ValidationFailure> failures) { }
        }

        private sealed class OptionsController : ImportListController
        {
            public OptionsController() : base(InterfaceProxy.Proxy<IImportListFactory>((method, args) => new List<ImportListDefinition>()), null, null, null) { }
            public FluentValidation.Results.ValidationResult Validate(ImportListResource resource) => SharedValidator.Validate(resource);
        }

        [TestCase("none", true)]
        [TestCase("specificBook", true)]
        [TestCase("entireAuthor", true)]
        [TestCase("999", false)]
        public void api_should_reject_undefined_legacy_monitoring_policies(string policy, bool valid)
        {
            var encoded = int.TryParse(policy, out _) ? policy : "\"" + policy + "\"";
            var resource = NzbDrone.Common.Serializer.STJson.Deserialize<ImportListResource>("{\"shouldMonitor\":" + encoded + "}");
            resource.Name = "Options";
            resource.Implementation = "GoodreadsBookshelf";
            resource.ConfigContract = nameof(GoodreadsBookshelfImportListSettings);
            var result = new OptionsController().Validate(resource);
            Assert.That(result.Errors.Where(error => error.PropertyName == nameof(ImportListResource.ShouldMonitor)),
                valid ? Is.Empty : Is.Not.Empty);
        }

        [TestCase("{\"audiobookMonitor\":999}", false)]
        [TestCase("{\"ebookMonitorNewItems\":999}", false)]
        [TestCase("{\"audiobookMonitor\":\"none\",\"ebookMonitor\":\"missing\",\"ebookMonitorNewItems\":\"new\"}", true)]
        [TestCase("{\"audiobookMonitor\":null,\"ebookMonitorNewItems\":null}", true)]
        [TestCase("{\"audiobookMonitor\":\"specificBook\",\"ebookMonitor\":\"specificBook\"}", true)]
        [TestCase("null", true)]
        public void api_should_validate_monitoring_policies_before_save(string optionsJson, bool valid)
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<ImportListMonitoringOptions>(optionsJson, NzbDrone.Common.Serializer.STJson.GetSerializerSettings());
            var result = new OptionsController().Validate(new ImportListResource
            {
                Name = "Options", Implementation = "GoodreadsBookshelf", ConfigContract = nameof(GoodreadsBookshelfImportListSettings), MonitoringOptions = options
            });
            Assert.That(result.Errors.Where(error => error.PropertyName.StartsWith("MonitoringOptions", StringComparison.Ordinal)),
                valid ? Is.Empty : Is.Not.Empty);
        }

        [OneTimeSetUp]
        public void InitializeSchemaBuilder()
        {
            using var container = new Container();
            container.RegisterInstance<ILocalizationService>(InterfaceProxy.Proxy<ILocalizationService>((method, args) => args[0]));
            SchemaBuilder.Initialize(container);
        }

        [TestCase(ImportListMonitorType.None, false)]
        [TestCase(ImportListMonitorType.SpecificBook, false)]
        [TestCase(ImportListMonitorType.SpecificBook, true)]
        [TestCase(ImportListMonitorType.EntireAuthor, true)]
        public void omitted_options_should_leave_format_policies_inherited_without_reconstructing_legacy_choices(ImportListMonitorType mode, bool keep)
        {
            var list = new ImportListDefinition { ShouldMonitor = mode, ShouldMonitorExisting = keep, MonitorNewItems = NewItemMonitorTypes.New };
            var options = list.GetMonitoringOptions();
            Assert.Multiple(() =>
            {
                Assert.That(options.AddMissingAuthors, Is.True);
                Assert.That(options.ResumePausedAuthors, Is.False);
                Assert.That(options.AudiobookMonitor, Is.Null);
                Assert.That(options.EbookMonitor, Is.Null);
                Assert.That(options.AudiobookMonitored, Is.Null);
                Assert.That(options.EbookMonitored, Is.Null);
                Assert.That(options.AudiobookMonitorNewItems, Is.Null);
                Assert.That(options.EbookMonitorNewItems, Is.Null);
                Assert.That(list.MonitoringOptions, Is.Null);
                Assert.That(list.ShouldMonitor, Is.EqualTo(mode));
                Assert.That(list.ShouldMonitorExisting, Is.EqualTo(keep));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void migration_should_leave_authors_paused_and_save_independent_options(bool postgres, bool previousVersionRecorded)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"list_options_{Guid.NewGuid():N}.db");
            var schema = "list_options_" + Guid.NewGuid().ToString("N");
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrEmpty(adminString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL import-list persistence");
            }

            var connectionString = postgres
                ? new NpgsqlConnectionStringBuilder(adminString) { Database = schema, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{schema}\"");
            }

            IDbConnection Open()
            {
                IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            }

            try
            {
                var controller = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 110),
                    postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                using (var connection = Open())
                {
                    if (previousVersionRecorded)
                    {
                        connection.Execute(@"INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"") VALUES (111, @AppliedOn, 'guard_edition_fts_renames')",
                            new { AppliedOn = DateTime.UtcNow });
                    }

                    connection.Execute(@"INSERT INTO ""RootFolders"" (""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"")
                        VALUES (@Path, 0, @Audio, @Ebook)", new
                    {
                        Path = @"C:\library",
                        Audio = "{\"qualityProfileId\":1,\"metadataProfileId\":1,\"monitored\":false,\"monitorExistingMode\":\"missing\"}",
                        Ebook = "{\"qualityProfileId\":1,\"metadataProfileId\":1,\"monitorExisting\":0}"
                    });
                    connection.Execute(@"INSERT INTO ""RootFolders"" (""Path"", ""FolderType"", ""AudiobookSettings"") VALUES (@Path, 1, @Settings)",
                        new { Path = @"C:\second", Settings = "{\"qualityProfileId\":1,\"metadataProfileId\":1,\"monitored\":true}" });
                    foreach (var pair in new[] { (Path: "/data/Books", Monitored: false), (Path: "/data/books", Monitored: true) })
                    {
                        connection.Execute(@"INSERT INTO ""RootFolders"" (""Path"", ""FolderType"", ""AudiobookSettings"") VALUES (@Path, 1, @Settings)",
                            new { pair.Path, Settings = Newtonsoft.Json.JsonConvert.SerializeObject(new { monitored = pair.Monitored }) });
                        connection.Execute(@"INSERT INTO ""ImportLists"" (""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""ConfigContract"", ""EnableAutomaticAdd"", ""ShouldSearch"", ""QualityProfileId"", ""MetadataProfileId"", ""RootFolderPath"", ""Settings"")
                            VALUES (@Name, 'GoodreadsBookshelf', 0, @Keep, 2, 'GoodreadsBookshelfImportListSettings', @Keep, @Keep, 1, 1, @Path, '{}')",
                            new { Name = "case-" + pair.Monitored, Keep = true, pair.Path });
                    }

                    connection.Execute(@"INSERT INTO ""RootFolders"" (""Path"", ""FolderType"", ""AudiobookSettings"") VALUES ('/', 1, @Settings)",
                        new { Settings = "{\"monitored\":false}" });
                    foreach (var pathValue in new[] { (Name: "root-none", Path: (string)null), (Name: "root-slash", Path: "/") })
                    {
                        connection.Execute(@"INSERT INTO ""ImportLists"" (""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""ConfigContract"", ""EnableAutomaticAdd"", ""ShouldSearch"", ""QualityProfileId"", ""MetadataProfileId"", ""RootFolderPath"", ""Settings"")
                            VALUES (@Name, 'GoodreadsBookshelf', 0, @Keep, 2, 'GoodreadsBookshelfImportListSettings', @Keep, @Keep, 1, 1, @Path, '{}')",
                            new { pathValue.Name, pathValue.Path, Keep = true });
                    }

                    foreach (var mode in new[] { ImportListMonitorType.None, ImportListMonitorType.SpecificBook, ImportListMonitorType.EntireAuthor })
                    {
                        connection.Execute(@"INSERT INTO ""ImportLists"" (""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""ConfigContract"", ""EnableAutomaticAdd"", ""ShouldSearch"", ""QualityProfileId"", ""MetadataProfileId"", ""RootFolderPath"", ""Settings"")
                            VALUES (@Name, 'GoodreadsBookshelf', @Mode, @Keep, 2, 'GoodreadsBookshelfImportListSettings', @Keep, @Keep, 1, 1, @Path, @Settings)",
                            new { Name = mode.ToString(), Mode = (int)mode, Keep = true, Path = @"C:\library",
                                Settings = mode == ImportListMonitorType.EntireAuthor ? Newtonsoft.Json.JsonConvert.SerializeObject(new { audiobookRootFolderPath = @"C:\second" }) : "{}" });
                    }

                    foreach (var settings in new[] { "{broken", "[]", Newtonsoft.Json.JsonConvert.SerializeObject(new { audiobookRootFolderPath = new { path = @"C:\second" } }), Newtonsoft.Json.JsonConvert.SerializeObject(new { audiobookRootFolderPath = new[] { @"C:\second" } }), "" })
                    {
                        connection.Execute(@"INSERT INTO ""ImportLists"" (""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""ConfigContract"", ""EnableAutomaticAdd"", ""ShouldSearch"", ""QualityProfileId"", ""MetadataProfileId"", ""RootFolderPath"", ""Settings"")
                            VALUES (@Name, 'GoodreadsBookshelf', 0, @Keep, 2, 'GoodreadsBookshelfImportListSettings', @Keep, @Keep, 1, 1, @Path, @Settings)",
                            new { Name = "tolerant-" + Guid.NewGuid().ToString("N"), Path = @"C:\library", Settings = settings, Keep = true });
                    }

                    connection.Execute(@"INSERT INTO ""ImportLists"" (""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""ConfigContract"", ""EnableAutomaticAdd"", ""ShouldSearch"", ""QualityProfileId"", ""MetadataProfileId"", ""RootFolderPath"", ""Settings"")
                        VALUES ('SpecificBook-off', 'GoodreadsBookshelf', 1, @Keep, 2, 'GoodreadsBookshelfImportListSettings', @Enabled, @Enabled, 1, 1, @Path, '{}')", new { Path = @"C:\library", Keep = false, Enabled = true });
                }

                if (previousVersionRecorded)
                {
                    controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 111),
                        postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                    using var connection = Open();
                    Assert.That(connection.ExecuteScalar<string>(@"SELECT ""Description"" FROM ""VersionInfo"" WHERE ""Version"" = 111"),
                        Is.EqualTo("guard_edition_fts_renames"));
                    var columnCount = postgres
                        ? connection.ExecuteScalar<long>(@"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'PendingAuthorImport' AND column_name = 'AudiobookNarrations'")
                        : connection.ExecuteScalar<long>(@"SELECT COUNT(*) FROM pragma_table_info('PendingAuthorImport') WHERE name = 'AudiobookNarrations'");
                    Assert.That(columnCount, Is.Zero, "A rebuilt migration cannot override an older receipt with the same number");
                    // Development receipts are reset explicitly, never by the production migration.
                    connection.Execute(@"DELETE FROM ""VersionInfo"" WHERE ""Version"" = 111 AND ""Description"" = 'guard_edition_fts_renames'");
                }

                controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 111),
                    postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                using (var connection = Open())
                {
                    Assert.That(connection.ExecuteScalar<string>(@"SELECT ""Description"" FROM ""VersionInfo"" WHERE ""Version"" = 111"),
                        Is.EqualTo("add_import_list_monitoring_options"));
                    Assert.DoesNotThrow(() => connection.Query(@"SELECT ""AudiobookNarrations"" FROM ""PendingAuthorImport"" LIMIT 1").ToList());
                }
                if (TableMapping.Mapper.TableMap.Count == 0)
                {
                    TableMapping.Map();
                }

                using (var connection = Open())
                {
                    var tolerant = connection.Query<string>(@"SELECT ""MonitoringOptions"" FROM ""ImportLists"" WHERE ""Name"" LIKE 'tolerant-%'").ToList();
                    Assert.That(tolerant, Has.Count.EqualTo(5));
                    Assert.That(tolerant.All(json => Newtonsoft.Json.JsonConvert.DeserializeObject<ImportListMonitoringOptions>(json).AudiobookMonitored == false), Is.True);
                    connection.Execute(@"DELETE FROM ""ImportLists"" WHERE ""Name"" LIKE 'tolerant-%'");
                }

                ImportListRepository Repository() => new(new MainDatabase(new Database("import-list-options", Open)), null);
                var repository = Repository();
                var migrated = repository.All();
                Assert.That(migrated, Has.Count.EqualTo(8));
                Assert.That(migrated.Single(list => list.Name == "root-none").MonitoringOptions.AudiobookMonitored, Is.True);
                Assert.That(migrated.Single(list => list.Name == "root-slash").MonitoringOptions.AudiobookMonitored, Is.False);
                var off = migrated.Single(list => list.Name == "SpecificBook-off");
                Assert.That(off.ShouldMonitorExisting, Is.False);
                Assert.That(off.MonitoringOptions.ResumePausedAuthors, Is.False);
                Assert.That(migrated.Single(list => list.Name == "case-False").MonitoringOptions.AudiobookMonitored, Is.False);
                Assert.That(migrated.Single(list => list.Name == "case-True").MonitoringOptions.AudiobookMonitored, Is.True);
                foreach (var list in migrated.Where(list => !list.Name.StartsWith("case-", StringComparison.Ordinal) && !list.Name.StartsWith("tolerant-", StringComparison.Ordinal) && list.Name != "SpecificBook-off" && !list.Name.StartsWith("root-", StringComparison.Ordinal)))
                {
                    var options = list.MonitoringOptions;
                    Assert.Multiple(() =>
                    {
                        Assert.That(options, Is.Not.Null);
                        Assert.That(options.AddMissingAuthors, Is.True);
                        Assert.That(list.ShouldMonitorExisting, Is.EqualTo(list.ShouldMonitor != ImportListMonitorType.None));
                        Assert.That(options.ResumePausedAuthors, Is.False, "An upgrade must leave paused authors paused");
                        Assert.That(options.AudiobookMonitored, Is.EqualTo(list.ShouldMonitor != ImportListMonitorType.None));
                        Assert.That(options.EbookMonitored, Is.EqualTo(list.ShouldMonitor == ImportListMonitorType.SpecificBook));
                        Assert.That(options.AudiobookMonitor, Is.EqualTo(list.ShouldMonitor == ImportListMonitorType.EntireAuthor ? MonitorTypes.All : MonitorTypes.None));
                        Assert.That(options.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                        Assert.That(options.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    });
                }

                using (var connection = Open())
                {
                    connection.Execute(@"UPDATE ""RootFolders"" SET ""AudiobookSettings"" = @Settings, ""EbookSettings"" = @Settings",
                        new { Settings = "{\"monitored\":true}" });
                }

                Assert.That(Repository().All().Single(list => list.Name == ImportListMonitorType.None.ToString()).GetMonitoringOptions().AudiobookMonitored, Is.False,
                    "Later root edits must not change unopened saved lists");
                var saved = migrated.Single(list => list.Name == ImportListMonitorType.SpecificBook.ToString());
                Assert.That(saved.MonitoringOptions, Is.Not.Null);
                Assert.That(saved.GetMonitoringOptions().ResumePausedAuthors, Is.False);
                saved.Settings = new GoodreadsBookshelfImportListSettings();
                var mapper = new ImportListResourceMapper();
                var resource = mapper.ToResource(saved);
                resource.EnableAutomaticAdd = false;
                resource.MonitoringOptions = new ImportListMonitoringOptions
                {
                    AddMissingAuthors = false, ResumePausedAuthors = true,
                    AudiobookMonitored = false, EbookMonitored = true,
                    AudiobookMonitor = MonitorTypes.Existing, EbookMonitor = MonitorTypes.Missing,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None, EbookMonitorNewItems = NewItemMonitorTypes.New
                };
                repository.Update(mapper.ToModel(resource));
                var readback = mapper.ToResource(Repository().Get(saved.Id));
                Assert.Multiple(() =>
                {
                    Assert.That(readback.EnableAutomaticAdd, Is.False);
                    Assert.That(readback.MonitoringOptions.AddMissingAuthors, Is.False);
                    Assert.That(readback.MonitoringOptions.ResumePausedAuthors, Is.True, "Explicit opt-in must remain available after upgrade");
                    Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.Existing));
                    Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.Missing));
                    Assert.That(readback.MonitoringOptions.EbookMonitored, Is.True);
                    Assert.That(readback.MonitoringOptions.AudiobookMonitored, Is.False);
                    Assert.That(readback.MonitoringOptions.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                });
                var cacheConnections = 0;
                var cacheDatabase = InterfaceProxy.Proxy<IDatabase>((method, args) =>
                {
                    if (method.Name == "get_DatabaseType") return postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite;
                    if (method.Name == nameof(IDatabase.OpenConnection))
                    {
                        cacheConnections++;
                        return Open();
                    }
                    throw new AssertionException(method.Name);
                });
                var identities = new ImportListBookIdentityCacheRepository(new MainDatabase(cacheDatabase), null);
                var mappings = Enumerable.Range(0, 905).Select(index => new ImportListBookIdentityCache
                {
                    SourceProviderId = "gr:" + (6000 + index), BookProviderId = "gr:123", AuthorProviderId = "gr:789",
                    EditionHasNarration = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                }).ToList();
                identities.InsertMany(mappings);
                cacheConnections = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var foundMappings = identities.FindBySourceProviderIds(mappings.Select(mapping => mapping.SourceProviderId));
                Assert.That(foundMappings, Has.Count.EqualTo(905));
                Assert.That(foundMappings.All(mapping => mapping.EditionHasNarration == false), Is.True);
                Assert.That(cacheConnections, Is.EqualTo(postgres ? 1 : 2));
                TestContext.WriteLine("{0}: read {1} source mappings in {2} queries, {3} ms", postgres ? "PostgreSQL" : "SQLite", foundMappings.Count, cacheConnections, watch.ElapsedMilliseconds);
                var classificationTime = identities.FindBySourceProviderId(mappings[0].SourceProviderId).UpdatedAt;
                identities.UpsertBySourceProviderId(new ImportListBookIdentityCache
                {
                    SourceProviderId = mappings[0].SourceProviderId, BookProviderId = "gr:123", AuthorProviderId = "gr:789", UpdatedAt = DateTime.UtcNow
                });
                Assert.That(identities.FindBySourceProviderId(mappings[0].SourceProviderId).EditionHasNarration, Is.False);
                Assert.That(identities.FindBySourceProviderId(mappings[0].SourceProviderId).UpdatedAt, Is.EqualTo(classificationTime));
                identities.UpsertBySourceProviderId(new ImportListBookIdentityCache
                {
                    SourceProviderId = mappings[0].SourceProviderId, BookProviderId = "gr:321", AuthorProviderId = "gr:789", UpdatedAt = DateTime.UtcNow
                });
                Assert.That(identities.FindBySourceProviderId(mappings[0].SourceProviderId).EditionHasNarration, Is.Null);
                var newSources = Enumerable.Range(0, 100).Select(index => new ImportListBookIdentityCache
                {
                    SourceProviderId = "gr:" + (8000 + index), BookProviderId = "gr:123", AuthorProviderId = "gr:789",
                    EditionHasNarration = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                }).ToList();
                cacheConnections = 0;
                watch.Restart();
                Assert.That(identities.FindBySourceProviderIds(newSources.Select(mapping => mapping.SourceProviderId)), Is.Empty);
                foreach (var mapping in newSources) identities.UpsertBySourceProviderId(mapping);
                Assert.That(cacheConnections, Is.EqualTo(201));
                TestContext.WriteLine("{0}: first 100 resolved sources, 1 batch read +100 source reads +100 inserts, {1} ms", postgres ? "PostgreSQL" : "SQLite", watch.ElapsedMilliseconds);
                cacheConnections = 0;
                watch.Restart();
                Assert.That(identities.FindBySourceProviderIds(newSources.Select(mapping => mapping.SourceProviderId)), Has.Count.EqualTo(100));
                Assert.That(cacheConnections, Is.EqualTo(1));
                TestContext.WriteLine("{0}: unchanged 100 resolved sources, 1 batch read, {1} ms", postgres ? "PostgreSQL" : "SQLite", watch.ElapsedMilliseconds);

                const string narrationSource = "edition:gr:456";
                ImportListBookIdentityCache SaveNarration(bool? classification, List<string> names = null,
                    string work = "gr:123", string author = "gr:789")
                {
                    cacheConnections = 0;
                    var mapping = identities.UpsertBySourceProviderId(new ImportListBookIdentityCache
                    {
                        SourceProviderId = narrationSource, BookProviderId = work, AuthorProviderId = author,
                        EditionHasNarration = classification, EditionNarratorNames = names,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                    });
                    Assert.That(cacheConnections, Is.EqualTo(2), "Persisting narrator intent reuses the existing source read and write");
                    return mapping;
                }

                var firstCast = new List<string> { "Stephen Fry", "Jane O'Neill" };
                var narrationId = SaveNarration(true, firstCast).Id;
                var persisted = identities.FindBySourceProviderIds(new[] { narrationSource }).Single();
                Assert.That(persisted.EditionNarratorNames, Is.EqualTo(firstCast));
                var secondCast = new List<string> { "Jim Dale", "Jane O’Neill" };
                SaveNarration(true, secondCast);
                persisted = identities.FindBySourceProviderId(narrationSource);
                Assert.That(persisted.EditionNarratorNames, Is.EqualTo(secondCast), "A supplied cast replaces the old cast");
                var narrationTime = persisted.UpdatedAt;
                SaveNarration(null, new List<string>());
                persisted = identities.FindBySourceProviderId(narrationSource);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.Id, Is.EqualTo(narrationId));
                    Assert.That(persisted.EditionHasNarration, Is.True);
                    Assert.That(persisted.EditionNarratorNames, Is.EqualTo(secondCast));
                    Assert.That(persisted.UpdatedAt, Is.EqualTo(narrationTime));
                });
                SaveNarration(true);
                Assert.That(identities.FindBySourceProviderId(narrationSource).EditionNarratorNames, Is.EqualTo(secondCast));
                SaveNarration(false, firstCast);
                persisted = identities.FindBySourceProviderId(narrationSource);
                Assert.That(persisted.EditionNarratorNames, Is.Null.Or.Empty, "A non-narrated edition cannot retain a cached cast");
                SaveNarration(null);
                Assert.That(identities.FindBySourceProviderId(narrationSource).EditionNarratorNames, Is.Null.Or.Empty);
                SaveNarration(true, firstCast);
                SaveNarration(null, work: "gr:321");
                persisted = identities.FindBySourceProviderId(narrationSource);
                Assert.That(persisted.EditionHasNarration, Is.Null);
                Assert.That(persisted.EditionNarratorNames, Is.Null.Or.Empty, "A changed work cannot inherit narrator intent");
                SaveNarration(true, secondCast, work: "gr:321");
                SaveNarration(null, work: "gr:321", author: "gr:987");
                persisted = identities.FindBySourceProviderId(narrationSource);
                Assert.That(persisted.EditionHasNarration, Is.Null);
                Assert.That(persisted.EditionNarratorNames, Is.Null.Or.Empty, "A changed author cannot inherit narrator intent");
                Assert.That(persisted.Id, Is.EqualTo(narrationId));

                var retained = Repository().Get(saved.Id);
                retained.Implementation = nameof(OptionsList);
                repository.Update(retained);
                var omitted = mapper.ToResource(retained);
                omitted.MonitoringOptions = null;
                omitted.RootFolderPath = @"C:\changed";
                var provider = new OptionsList();
                var factory = new ImportListFactory(null, repository, new[] { provider },
                    InterfaceProxy.Proxy<IServiceProvider>((method, args) => provider), InterfaceProxy.Proxy<IEventAggregator>((method, args) => null),
                    null, null, LogManager.GetCurrentClassLogger());
                factory.Update(mapper.ToModel(omitted));
                var afterOmission = mapper.ToResource(Repository().Get(saved.Id));
                Assert.That(afterOmission.RootFolderPath, Is.EqualTo(@"C:\changed"));
                Assert.That(afterOmission.MonitoringOptions.AddMissingAuthors, Is.False);
                Assert.That(afterOmission.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(afterOmission.MonitoringOptions.EbookMonitored, Is.True);

            }
            finally
            {
                if (postgres)
                {
                    using var admin = new NpgsqlConnection(adminString);
                    admin.Open();
                    admin.Execute($"DROP DATABASE \"{schema}\"");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
