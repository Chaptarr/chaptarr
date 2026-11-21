using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Npgsql;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(111)]
    public class add_import_list_monitoring_options : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("ImportLists").AddColumn("MonitoringOptions").AsString(int.MaxValue).Nullable();
            Alter.Table("ImportListBookIdentityCache").AddColumn("EditionHasNarration").AsBoolean().Nullable();
            Alter.Table("ImportListBookIdentityCache").AddColumn("EditionNarratorNames").AsString(int.MaxValue).Nullable();
            Alter.Table("PendingAuthorImport").AddColumn("AudiobookNarrations").AsString(int.MaxValue).Nullable();
            Execute.WithConnection((connection, transaction) =>
            {
                ConvertImportListSettings(connection, transaction);
                BackfillEditionAliases(connection, transaction, _logger);
            });
        }

        internal void ConvertImportListSettings(IDbConnection connection, IDbTransaction transaction)
        {
            var rootRows = connection.Query<RootFolder>(@"SELECT ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"" FROM ""RootFolders""", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToList();
            var roots = rootRows.ToLookup(root => NormalizePath(root.Path), StringComparer.OrdinalIgnoreCase);
            var lists = connection.Query<LegacyList>(@"SELECT ""Id"", ""Name"", ""Implementation"", ""ShouldMonitor"", ""ShouldMonitorExisting"", ""MonitorNewItems"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"", ""Settings"", ""MonitoringOptions"" FROM ""ImportLists""", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToList();
            var hasGenericLists = lists.Any(IsGenericList);
            var qualityTypes = hasGenericLists ? connection.Query<QualityProfile>(@"SELECT ""Id"", ""ProfileType"" FROM ""QualityProfiles""", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToDictionary(profile => profile.Id, profile => profile.ProfileType) : new Dictionary<int, ProfileType>();
            var metadataTypes = hasGenericLists ? connection.Query<MetadataProfile>(@"SELECT ""Id"", ""ProfileType"" FROM ""MetadataProfiles""", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToDictionary(profile => profile.Id, profile => profile.ProfileType) : new Dictionary<int, MetadataProfileType>();
            var rootService = new RootFolderService(null, null, null, null, _logger);
            var resolver = new RootFolderSettingsResolver(null);
            var resolvedRoots = new Dictionary<(string Path, BookMediaType Media), ResolvedRootFolderSettings>();
            ResolvedRootFolderSettings ResolveRoot(string path, BookMediaType media)
            {
                if (string.IsNullOrWhiteSpace(path)) return resolver.ResolveSettings(null, media);
                var key = (path, media);
                if (!resolvedRoots.TryGetValue(key, out var resolved))
                {
                    resolved = resolver.ResolveSettings(rootService.GetBestRootFolder(path, rootRows), media);
                    resolvedRoots[key] = resolved;
                }
                return resolved;
            }
            var updates = lists.Select(list =>
            {
                var settings = TryParseSettings(list.Settings, list.Id, list.Name);
                MediaTypeSettings RootSettings(string media)
                {
                    var token = settings.GetValue(media + "RootFolderPath", StringComparison.OrdinalIgnoreCase);
                    var path = token?.Type == JTokenType.String ? token.Value<string>() : null;
                    var key = NormalizePath(string.IsNullOrWhiteSpace(path) ? list.RootFolderPath : path);
                    var matches = roots[key];
                    var root = matches.FirstOrDefault(candidate => NormalizePath(candidate.Path) == key) ?? matches.FirstOrDefault();
                    return media == "audiobook" ? root?.GetAudiobookSettings() : root?.GetEbookSettings();
                }

                var generic = IsGenericList(list);
                // Snapshot saved choices once; opening the editor must not change their meaning.
                var options = new ImportListMonitoringOptions
                {
                    AddMissingAuthors = true,
                    ResumePausedAuthors = false,
                    AudiobookMonitored = list.ShouldMonitor == ImportListMonitorType.SpecificBook ? true : generic ? null : RootSettings("audiobook")?.Monitored ?? true,
                    EbookMonitored = list.ShouldMonitor == ImportListMonitorType.SpecificBook ? true : generic ? null : RootSettings("ebook")?.Monitored ?? true,
                    AudiobookMonitor = list.ShouldMonitor == ImportListMonitorType.EntireAuthor ? MonitorTypes.All : MonitorTypes.None,
                    EbookMonitor = list.ShouldMonitor == ImportListMonitorType.EntireAuthor ? MonitorTypes.All : MonitorTypes.None,
                    AudiobookMonitorNewItems = list.MonitorNewItems,
                    EbookMonitorNewItems = list.MonitorNewItems
                };
                var tags = ParseTags(list);
                var settingsChanged = false;
                if (generic)
                {
                    var audio = ResolveRoot(list.RootFolderPath, BookMediaType.Audiobook);
                    var ebook = ResolveRoot(list.RootFolderPath, BookMediaType.Ebook);
                    void Set(string field, object value)
                    {
                        if (settings.GetValue(field, StringComparison.OrdinalIgnoreCase) != null) return;
                        settings[field] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                        settingsChanged = true;
                    }
                    void SetMedia(string media, ResolvedRootFolderSettings root, ProfileType qualityType, MetadataProfileType metadataType)
                    {
                        var quality = qualityTypes.GetValueOrDefault(list.QualityProfileId) == qualityType ? list.QualityProfileId : root.QualityProfileId ?? 0;
                        var metadata = metadataTypes.TryGetValue(list.MetadataProfileId, out var type) && (type == MetadataProfileType.General || type == metadataType)
                            ? list.MetadataProfileId : root.MetadataProfileId ?? 0;
                        Set("monitor" + (media == "audiobook" ? "Audiobooks" : "Ebooks"), root.IsConfigured);
                        Set(media + "RootFolderPath", root.IsConfigured ? list.RootFolderPath : null);
                        Set(media + "QualityProfileId", root.IsConfigured ? quality : 0);
                        Set(media + "MetadataProfileId", root.IsConfigured ? metadata : 0);
                        Set(media + "Tags", root.IsConfigured ? tags : new List<int>());
                    }
                    SetMedia("audiobook", audio, ProfileType.Audiobook, MetadataProfileType.Audiobook);
                    SetMedia("ebook", ebook, ProfileType.Ebook, MetadataProfileType.Ebook);
                    options.AudiobookMonitored = list.ShouldMonitor == ImportListMonitorType.SpecificBook ? true : audio.IsConfigured ? audio.Monitored : null;
                    options.EbookMonitored = list.ShouldMonitor == ImportListMonitorType.SpecificBook ? true : ebook.IsConfigured ? ebook.Monitored : null;
                }
                else if (tags.Count > 0 && list.Implementation is "GoodreadsBookshelf" or "GoodreadsListImportList" or "GoodreadsSeriesImportList" or "HardcoverLibraryImportList")
                {
                    foreach (var media in new[] { "audiobook", "ebook" })
                    {
                        var enabled = settings.GetValue(media == "audiobook" ? "monitorAudiobooks" : "monitorEbooks", StringComparison.OrdinalIgnoreCase);
                        if (enabled != null && (enabled.Type != JTokenType.Boolean || !enabled.Value<bool>())) continue;
                        var field = media + "Tags";
                        var token = settings.GetValue(field, StringComparison.OrdinalIgnoreCase);
                        var merged = new JArray(((token as JArray)?.Values<int>() ?? Enumerable.Empty<int>()).Concat(tags).Distinct().OrderBy(tag => tag));
                        if (token == null) settings[field] = merged;
                        else token.Replace(merged);
                        settingsChanged = true;
                    }
                }
                return new
                {
                    list.Id,
                    Settings = settingsChanged ? settings.ToString(Formatting.None) : list.Settings,
                    MonitoringOptions = generic && !string.IsNullOrWhiteSpace(list.MonitoringOptions) ? list.MonitoringOptions : JsonConvert.SerializeObject(options),
                    ShouldMonitorExisting = list.ShouldMonitor != ImportListMonitorType.None && list.ShouldMonitorExisting
                };
            }).ToList();
            connection.Execute(@"UPDATE ""ImportLists"" SET ""Settings"" = @Settings, ""MonitoringOptions"" = @MonitoringOptions, ""ShouldMonitorExisting"" = @ShouldMonitorExisting WHERE ""Id"" = @Id", updates, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            _logger.Info("Converted monitoring settings for {0} import lists", updates.Count);
        }

        internal static void BackfillEditionAliases(IDbConnection connection, IDbTransaction transaction, Logger logger)
        {
            var timer = Stopwatch.StartNew();
            const int batchSize = SqliteVariableLimit.MaxParameters / 2;
            var afterId = 0;
            var aliasCount = 0;
            var editionCount = 0;
            var malformedCollections = 0;
            var failedEditions = 0;
            var reads = 0;
            var writes = 0;
            while (true)
            {
                var rows = connection.Query(@"SELECT ""Id"", ""ForeignEditionId"", ""HardcoverEditionId"", ""GoodreadsEditionId"", ""OpenLibraryEditionId"", ""GoogleBooksEditionId"", ""Asin"", ""AudibleASIN"", ""Asins"" FROM ""Editions"" WHERE ""Id"" > @AfterId ORDER BY ""Id"" LIMIT @BatchSize;",
                    new { AfterId = afterId, BatchSize = batchSize }, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).Cast<IDictionary<string, object>>().ToList();
                reads++;
                var aliases = new List<ProviderAlias>();
                foreach (IDictionary<string, object> row in rows)
                {
                    editionCount++;
                    try
                    {
                        string Value(string key) => row[key]?.ToString();
                        var edition = new Edition
                        {
                            Id = Convert.ToInt32(row["Id"]), ForeignEditionId = Value("ForeignEditionId"),
                            HardcoverEditionId = Value("HardcoverEditionId"), GoodreadsEditionId = long.TryParse(Value("GoodreadsEditionId"), out var gr) ? gr : null,
                            OpenLibraryEditionId = Value("OpenLibraryEditionId"), GoogleBooksEditionId = Value("GoogleBooksEditionId"),
                            Asin = Value("Asin"), AudibleASIN = Value("AudibleASIN")
                        };
                        try { edition.Asins = JsonConvert.DeserializeObject<List<string>>(Value("Asins") ?? "[]"); }
                        catch (JsonException ex)
                        {
                            malformedCollections++;
                            logger.Warn(ex, "Cannot parse stored edition ASIN collection for local edition {0}; canonical aliases remain eligible", edition.Id);
                        }
                        aliases.AddRange(ProviderAliasService.NormalizeAliases(BookEditionIdentity.GetEditionProviderIds(edition).Select(BookEditionIdentity.NormalizeRequestedEditionProviderId))
                            .Select(alias => new ProviderAlias { EntityId = edition.Id, Provider = alias.Provider, NormalizedProviderId = alias.NormalizedProviderId }));
                    }
                    catch (Exception ex)
                    {
                        failedEditions++;
                        logger.Warn(ex, "Cannot backfill aliases for local edition {0}; continuing remaining editions", row["Id"]);
                    }
                }
                aliasCount += aliases.Count;
                var parameterLimit = connection is NpgsqlConnection ? ushort.MaxValue : SqliteVariableLimit.MaxParameters;
                foreach (var chunk in aliases.DistinctBy(alias => (alias.EntityId, alias.Provider, alias.NormalizedProviderId)).Chunk(parameterLimit / 3))
                {
                    var parameters = new DynamicParameters();
                    var values = chunk.Select((alias, index) =>
                    {
                        parameters.Add($"id{index}", alias.EntityId);
                        parameters.Add($"provider{index}", alias.Provider);
                        parameters.Add($"key{index}", alias.NormalizedProviderId);
                        return $"('Edition', @id{index}, 'edition', @provider{index}, @key{index}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)";
                    });
                    writes++;
                    connection.Execute($"INSERT INTO \"ProviderAliasIndex\" (\"EntityType\", \"EntityId\", \"Scope\", \"Provider\", \"NormalizedProviderId\", \"CreatedAt\", \"UpdatedAt\") VALUES {string.Join(",", values)} ON CONFLICT (\"EntityType\", \"EntityId\", \"Scope\", \"Provider\", \"NormalizedProviderId\") DO NOTHING", parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }
                if (rows.Count < batchSize)
                {
                    break;
                }
                afterId = Convert.ToInt32(rows[^1]["Id"]);
            }
            logger.Info("Backfilled persisted edition aliases from {0} stored editions and {1} attested identifiers; read queries {2}, insert batches {3}, elapsed {4} ms", editionCount, aliasCount, reads, writes, timer.ElapsedMilliseconds);
            if (malformedCollections > 0 || failedEditions > 0)
            {
                logger.Warn("Edition alias backfill completed with {0} malformed ASIN collections and {1} failed editions out of {2} stored editions", malformedCollections, failedEditions, editionCount);
            }
        }

        private JObject TryParseSettings(string settings, int listId, string name)
        {
            if (string.IsNullOrWhiteSpace(settings))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(settings);
            }
            catch (JsonException)
            {
                _logger.Warn("Import list {0} ({1}): invalid legacy settings; using its saved root for monitoring conversion", listId, name);
                return new JObject();
            }
        }

        private List<int> ParseTags(LegacyList list)
        {
            try
            {
                return (JsonConvert.DeserializeObject<List<int>>(list.Tags ?? "[]") ?? new List<int>()).Distinct().OrderBy(tag => tag).ToList();
            }
            catch (JsonException)
            {
                _logger.Warn("Import list {0} ({1}): invalid legacy destination tags; continuing its settings conversion", list.Id, list.Name);
                return new List<int>();
            }
        }

        private static bool IsGenericList(LegacyList list) => list.Implementation is "ChaptarrImport" or "LazyLibrarianImport";

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var trimmed = path.TrimEnd('/', '\\');
            return trimmed.Length == 0 ? path : trimmed;
        }

        private sealed class LegacyList
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Implementation { get; set; }
            public ImportListMonitorType ShouldMonitor { get; set; }
            public bool ShouldMonitorExisting { get; set; }
            public NewItemMonitorTypes MonitorNewItems { get; set; }
            public string RootFolderPath { get; set; }
            public int QualityProfileId { get; set; }
            public int MetadataProfileId { get; set; }
            public string Tags { get; set; }
            public string Settings { get; set; }
            public string MonitoringOptions { get; set; }
        }
    }
}
