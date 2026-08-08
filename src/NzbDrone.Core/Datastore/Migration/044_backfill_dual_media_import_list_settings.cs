using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(44)]
    public class backfill_dual_media_import_list_settings : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var rows = connection.Query<ImportListRow>(
                    @"SELECT ""Id"",
                             ""Implementation"",
                             ""Settings"",
                             ""RootFolderPath"",
                             ""QualityProfileId"",
                             ""MetadataProfileId"",
                             ""Tags""
                      FROM ""ImportLists""
                      WHERE ""Implementation"" IN ('GoodreadsBookshelf', 'HardcoverLibraryImportList');",
                    transaction: transaction).ToList();

                foreach (var row in rows)
                {
                    var settingsObj = TryParseSettings(row.Settings);
                    var tagsFromColumn = ParseTags(row.Tags);

                    var changed = false;

                    if (row.Implementation == "GoodreadsBookshelf")
                    {
                        changed |= BackfillGoodreadsBookshelf(settingsObj, row, tagsFromColumn);
                    }
                    else if (row.Implementation == "HardcoverLibraryImportList")
                    {
                        changed |= BackfillHardcoverLibrary(settingsObj, row, tagsFromColumn);
                    }

                    if (!changed)
                    {
                        continue;
                    }

                    connection.Execute(
                        @"UPDATE ""ImportLists""
                          SET ""Settings"" = @Settings
                          WHERE ""Id"" = @Id;",
                        new
                        {
                            row.Id,
                            Settings = settingsObj.ToString(Formatting.Indented)
                        },
                        transaction: transaction);
                }
            });
        }

        private static bool BackfillGoodreadsBookshelf(JObject settingsObj, ImportListRow row, List<int> tagsFromColumn)
        {
            var changed = false;

            // Root folder + profiles were historically stored on ImportLists for all non-Hardcover lists.
            changed |= SetIfMissingOrEmpty(settingsObj, "audiobookRootFolderPath", row.RootFolderPath);
            changed |= SetIfMissingOrEmpty(settingsObj, "ebookRootFolderPath", row.RootFolderPath);

            changed |= SetIfMissing(settingsObj, "audiobookQualityProfileId", row.QualityProfileId);
            changed |= SetIfMissing(settingsObj, "ebookQualityProfileId", row.QualityProfileId);
            changed |= SetIfMissing(settingsObj, "audiobookMetadataProfileId", row.MetadataProfileId);
            changed |= SetIfMissing(settingsObj, "ebookMetadataProfileId", row.MetadataProfileId);

            // Tags historically lived on ProviderDefinition.Tags for import lists.
            changed |= SetTagsIfMissingOrEmpty(settingsObj, "audiobookTags", tagsFromColumn);
            changed |= SetTagsIfMissingOrEmpty(settingsObj, "ebookTags", tagsFromColumn);

            return changed;
        }

        private static bool BackfillHardcoverLibrary(JObject settingsObj, ImportListRow row, List<int> tagsFromColumn)
        {
            var changed = false;

            var monitorAudiobooks = ReadBool(settingsObj, "monitorAudiobooks", defaultValue: true);
            var monitorEbooks = ReadBool(settingsObj, "monitorEbooks", defaultValue: true);

            // Older builds allowed these to be empty because the UI wrote fallbacks into ImportLists.RootFolderPath.
            if (monitorAudiobooks)
            {
                changed |= SetIfMissingOrEmpty(settingsObj, "audiobookRootFolderPath", row.RootFolderPath);
            }

            if (monitorEbooks)
            {
                changed |= SetIfMissingOrEmpty(settingsObj, "ebookRootFolderPath", row.RootFolderPath);
            }

            // If the user previously used the generic import list tags, copy into per-media tags only when those are empty.
            changed |= SetTagsIfMissingOrEmpty(settingsObj, "audiobookTags", tagsFromColumn);
            changed |= SetTagsIfMissingOrEmpty(settingsObj, "ebookTags", tagsFromColumn);

            return changed;
        }

        private static JObject TryParseSettings(string settings)
        {
            if (settings.IsNullOrWhiteSpace())
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(settings);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        private static List<int> ParseTags(string tags)
        {
            if (tags.IsNullOrWhiteSpace())
            {
                return new List<int>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<int>>(tags) ?? new List<int>();
            }
            catch (JsonException)
            {
                return new List<int>();
            }
        }

        private static bool ReadBool(JObject obj, string propertyName, bool defaultValue)
        {
            if (obj == null || propertyName.IsNullOrWhiteSpace())
            {
                return defaultValue;
            }

            if (!obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var token) || token == null)
            {
                return defaultValue;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() != 0;
            }

            if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private static bool SetIfMissing(JObject obj, string propertyName, int value)
        {
            if (obj == null || propertyName.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (obj.Properties().Any(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            obj[propertyName] = value;
            return true;
        }

        private static bool SetIfMissingOrEmpty(JObject obj, string propertyName, string value)
        {
            if (obj == null || propertyName.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var token) ||
                token == null ||
                token.Type == JTokenType.Null ||
                (token.Type == JTokenType.String && token.Value<string>().IsNullOrWhiteSpace()))
            {
                if (value.IsNullOrWhiteSpace())
                {
                    return false;
                }

                obj[propertyName] = value;
                return true;
            }

            return false;
        }

        private static bool SetTagsIfMissingOrEmpty(JObject obj, string propertyName, List<int> tags)
        {
            if (obj == null || propertyName.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (tags == null || tags.Count == 0)
            {
                return false;
            }

            if (obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var existing) &&
                existing is JArray existingArray &&
                existingArray.Count > 0)
            {
                return false;
            }

            obj[propertyName] = new JArray(tags.Distinct().OrderBy(t => t));
            return true;
        }

        private sealed class ImportListRow
        {
            public int Id { get; set; }
            public string Implementation { get; set; }
            public string Settings { get; set; }
            public string RootFolderPath { get; set; }
            public int QualityProfileId { get; set; }
            public int MetadataProfileId { get; set; }
            public string Tags { get; set; }
        }
    }
}

