using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(85)]
    public class backfill_goodreads_list_series_dual_media_settings : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                GoodreadsDualMediaImportListSettingsBackfill.Apply(connection, transaction);
            });
        }
    }

    internal static class GoodreadsDualMediaImportListSettingsBackfill
    {
        public static void Apply(IDbConnection connection, IDbTransaction transaction)
        {
            var qualityProfileTypesById = connection.Query<QualityProfileRow>(
                @"SELECT ""Id"", ""ProfileType"" FROM ""QualityProfiles"";",
                transaction: transaction).ToDictionary(x => x.Id, x => x.ProfileType);

            var metadataProfileTypesById = connection.Query<MetadataProfileRow>(
                @"SELECT ""Id"", ""ProfileType"" FROM ""MetadataProfiles"";",
                transaction: transaction).ToDictionary(x => x.Id, x => x.ProfileType);

            var rootFolders = connection.Query<RootFolderRow>(
                @"SELECT ""Path"", ""FolderType""
                  FROM ""RootFolders"";",
                transaction: transaction).ToList();

            var rows = connection.Query<ImportListRow>(
                    @"SELECT ""Id"",
                             ""Settings"",
                             ""RootFolderPath"",
                             ""QualityProfileId"",
                             ""MetadataProfileId"",
                             ""Tags""
                      FROM ""ImportLists""
                      WHERE ""Implementation"" IN ('GoodreadsListImportList', 'GoodreadsSeriesImportList');",
                    transaction: transaction).ToList();

            foreach (var row in rows)
            {
                var settingsObj = TryParseSettings(row.Settings);
                var tagsFromColumn = ParseTags(row.Tags);
                var rootFolder = FindRootFolder(row.RootFolderPath, rootFolders);
                var mediaTargets = ResolveMediaTargets(rootFolder, row.QualityProfileId, qualityProfileTypesById);

                var changed = false;

                changed |= SetIfMissing(settingsObj, "monitorAudiobooks", mediaTargets.Audiobook);
                changed |= SetIfMissing(settingsObj, "monitorEbooks", mediaTargets.Ebook);

                if (mediaTargets.Audiobook)
                {
                    changed |= SetIfMissingOrEmpty(settingsObj, "audiobookRootFolderPath", row.RootFolderPath);

                    if (IsValidQualityProfileId(row.QualityProfileId, ProfileType.Audiobook, qualityProfileTypesById))
                    {
                        changed |= SetIfMissing(settingsObj, "audiobookQualityProfileId", row.QualityProfileId);
                    }

                    if (IsValidMetadataProfileId(row.MetadataProfileId, metadataProfileTypesById, MetadataProfileType.General, MetadataProfileType.Audiobook))
                    {
                        changed |= SetIfMissing(settingsObj, "audiobookMetadataProfileId", row.MetadataProfileId);
                    }

                    changed |= SetTagsIfMissingOrEmpty(settingsObj, "audiobookTags", tagsFromColumn);
                }

                if (mediaTargets.Ebook)
                {
                    changed |= SetIfMissingOrEmpty(settingsObj, "ebookRootFolderPath", row.RootFolderPath);

                    if (IsValidQualityProfileId(row.QualityProfileId, ProfileType.Ebook, qualityProfileTypesById))
                    {
                        changed |= SetIfMissing(settingsObj, "ebookQualityProfileId", row.QualityProfileId);
                    }

                    if (IsValidMetadataProfileId(row.MetadataProfileId, metadataProfileTypesById, MetadataProfileType.General, MetadataProfileType.Ebook))
                    {
                        changed |= SetIfMissing(settingsObj, "ebookMetadataProfileId", row.MetadataProfileId);
                    }

                    changed |= SetTagsIfMissingOrEmpty(settingsObj, "ebookTags", tagsFromColumn);
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

        private static bool SetIfMissing(JObject obj, string propertyName, bool value)
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

        private static RootFolderRow FindRootFolder(string rootFolderPath, List<RootFolderRow> rootFolders)
        {
            var key = NormalizePathKey(rootFolderPath);
            if (key.IsNullOrWhiteSpace() || rootFolders == null)
            {
                return null;
            }

            return rootFolders
                .Select(r => new { RootFolder = r, Key = NormalizePathKey(r.Path) })
                .Where(r => r.Key.IsNotNullOrWhiteSpace() && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Key.Length)
                .Select(r => r.RootFolder)
                .FirstOrDefault();
        }

        private static (bool Audiobook, bool Ebook) ResolveMediaTargets(RootFolderRow rootFolder, int qualityProfileId, Dictionary<int, ProfileType> profileTypesById)
        {
            if (rootFolder != null)
            {
                return rootFolder.FolderType switch
                {
                    FolderType.Audiobook => (true, false),
                    FolderType.Ebook => (false, true),
                    _ => (true, true)
                };
            }

            if (profileTypesById.TryGetValue(qualityProfileId, out var profileType))
            {
                return profileType == ProfileType.Audiobook ? (true, false) : (false, true);
            }

            return (true, true);
        }

        private static bool IsValidQualityProfileId(int profileId, ProfileType expectedType, Dictionary<int, ProfileType> profileTypesById)
        {
            return profileId > 0 &&
                   profileTypesById.TryGetValue(profileId, out var profileType) &&
                   profileType == expectedType;
        }

        private static bool IsValidMetadataProfileId(int profileId, Dictionary<int, MetadataProfileType> profileTypesById, params MetadataProfileType[] allowedTypes)
        {
            return profileId > 0 &&
                   profileTypesById.TryGetValue(profileId, out var profileType) &&
                   allowedTypes.Contains(profileType);
        }

        private static string NormalizePathKey(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var trimmed = path.TrimEnd('/', '\\');
            return trimmed.IsNullOrWhiteSpace() ? path : trimmed;
        }

        private sealed class QualityProfileRow
        {
            public int Id { get; set; }
            public ProfileType ProfileType { get; set; }
        }

        private sealed class MetadataProfileRow
        {
            public int Id { get; set; }
            public MetadataProfileType ProfileType { get; set; }
        }

        private sealed class RootFolderRow
        {
            public string Path { get; set; }
            public FolderType FolderType { get; set; }
        }

        private sealed class ImportListRow
        {
            public int Id { get; set; }
            public string Settings { get; set; }
            public string RootFolderPath { get; set; }
            public int QualityProfileId { get; set; }
            public int MetadataProfileId { get; set; }
            public string Tags { get; set; }
        }
    }
}
