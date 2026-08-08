using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(34)]
    public class backfill_metadata_profile_ids : NzbDroneMigrationBase
    {
        private const string NoneProfileName = "None";
        private const string AudiobookDefaultProfileName = "Audiobook Default";
        private const string EbookDefaultProfileName = "Ebook Default";

        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var profiles = connection.Query<MetadataProfileRow>(
                    @"SELECT ""Id"", ""Name"", ""ProfileType"" FROM ""MetadataProfiles"";",
                    transaction: transaction).ToList();

                var profileTypeById = profiles.ToDictionary(x => x.Id, x => x.ProfileType);
                var audiobookDefaultProfileId = GetDefaultProfileId(profiles, profileType: 1, preferredName: AudiobookDefaultProfileName);
                var ebookDefaultProfileId = GetDefaultProfileId(profiles, profileType: 2, preferredName: EbookDefaultProfileName);

                if (!audiobookDefaultProfileId.HasValue || !ebookDefaultProfileId.HasValue)
                {
                    // These should always exist due to profile deletion guards, but fail safely if something is off.
                    return;
                }

                var rootFolders = connection.Query<RootFolderRow>(
                    @"SELECT ""Id"", ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"" FROM ""RootFolders"";",
                    transaction: transaction).ToList();

                // Backfill missing MetadataProfileId in root folder JSON settings
                foreach (var rootFolder in rootFolders)
                {
                    var supportsAudiobook = rootFolder.FolderType == 0 || rootFolder.FolderType == 1;
                    var supportsEbook = rootFolder.FolderType == 0 || rootFolder.FolderType == 2;

                    if (supportsAudiobook)
                    {
                        var allowedProfileTypes = new HashSet<int> { 0, 1 };
                        if (TryEnsureMetadataProfileId(rootFolder.AudiobookSettings, audiobookDefaultProfileId.Value, allowedProfileTypes, profileTypeById, out var updatedSettings))
                        {
                            rootFolder.AudiobookSettings = updatedSettings;
                            connection.Execute(
                                @"UPDATE ""RootFolders"" SET ""AudiobookSettings"" = @AudiobookSettings WHERE ""Id"" = @Id;",
                                new { rootFolder.AudiobookSettings, rootFolder.Id },
                                transaction: transaction);
                        }
                    }

                    if (supportsEbook)
                    {
                        var allowedProfileTypes = new HashSet<int> { 0, 2 };
                        if (TryEnsureMetadataProfileId(rootFolder.EbookSettings, ebookDefaultProfileId.Value, allowedProfileTypes, profileTypeById, out var updatedSettings))
                        {
                            rootFolder.EbookSettings = updatedSettings;
                            connection.Execute(
                                @"UPDATE ""RootFolders"" SET ""EbookSettings"" = @EbookSettings WHERE ""Id"" = @Id;",
                                new { rootFolder.EbookSettings, rootFolder.Id },
                                transaction: transaction);
                        }
                    }
                }

                var audiobookRootFolderProfiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ebookRootFolderProfiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var rootFolder in rootFolders)
                {
                    var normalizedPath = NormalizePathKey(rootFolder.Path);
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        continue;
                    }

                    if (TryReadMetadataProfileId(rootFolder.AudiobookSettings, out var audiobookProfileId) && audiobookProfileId.HasValue)
                    {
                        audiobookRootFolderProfiles[normalizedPath] = audiobookProfileId.Value;
                    }

                    if (TryReadMetadataProfileId(rootFolder.EbookSettings, out var ebookProfileId) && ebookProfileId.HasValue)
                    {
                        ebookRootFolderProfiles[normalizedPath] = ebookProfileId.Value;
                    }
                }

                var authors = connection.Query<AuthorRow>(
                    @"SELECT ""Id"",
                             ""AudiobookRootFolderPath"",
                             ""EbookRootFolderPath"",
                             ""AudiobookMetadataProfileId"",
                             ""EbookMetadataProfileId"",
                             ""AudiobookQualityProfileId"",
                             ""EbookQualityProfileId"",
                             ""AudiobookMonitorExisting"",
                             ""EbookMonitorExisting"",
                             ""AudiobookPath"",
                             ""EbookPath""
                      FROM ""Authors"";",
                    transaction: transaction).ToList();

                foreach (var author in authors)
                {
                    var audiobookConfigured = IsMediaTypeConfigured(author.AudiobookRootFolderPath, author.AudiobookQualityProfileId, author.AudiobookMonitorExisting, author.AudiobookPath);
                    var ebookConfigured = IsMediaTypeConfigured(author.EbookRootFolderPath, author.EbookQualityProfileId, author.EbookMonitorExisting, author.EbookPath);

                    int? audiobookProfileToSet = null;
                    int? ebookProfileToSet = null;

                    if (audiobookConfigured && !author.AudiobookMetadataProfileId.HasValue)
                    {
                        audiobookProfileToSet = ResolveProfileId(author.AudiobookRootFolderPath, audiobookRootFolderProfiles, audiobookDefaultProfileId.Value);
                    }

                    if (ebookConfigured && !author.EbookMetadataProfileId.HasValue)
                    {
                        ebookProfileToSet = ResolveProfileId(author.EbookRootFolderPath, ebookRootFolderProfiles, ebookDefaultProfileId.Value);
                    }

                    if (audiobookProfileToSet.HasValue || ebookProfileToSet.HasValue)
                    {
                        connection.Execute(
                            @"UPDATE ""Authors""
                              SET ""AudiobookMetadataProfileId"" = COALESCE(""AudiobookMetadataProfileId"", @AudiobookMetadataProfileId),
                                  ""EbookMetadataProfileId"" = COALESCE(""EbookMetadataProfileId"", @EbookMetadataProfileId)
                              WHERE ""Id"" = @Id;",
                            new
                            {
                                author.Id,
                                AudiobookMetadataProfileId = audiobookProfileToSet,
                                EbookMetadataProfileId = ebookProfileToSet
                            },
                            transaction: transaction);
                    }
                }
            });
        }

        private static int? GetDefaultProfileId(List<MetadataProfileRow> profiles, int profileType, string preferredName)
        {
            var preferred = profiles
                .Where(x => x.ProfileType == profileType)
                .Where(x => x.Name == preferredName)
                .OrderBy(x => x.Id)
                .FirstOrDefault();

            if (preferred != null)
            {
                return preferred.Id;
            }

            return profiles
                .Where(x => x.ProfileType == profileType)
                .Where(x => x.Name != NoneProfileName)
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefault();
        }

        private static bool TryEnsureMetadataProfileId(string settingsJson,
                                                      int defaultProfileId,
                                                      HashSet<int> allowedProfileTypes,
                                                      Dictionary<int, int> profileTypeById,
                                                      out string updatedSettingsJson)
        {
            updatedSettingsJson = settingsJson;

            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return false;
            }

            try
            {
                var settings = JObject.Parse(settingsJson);
                var metadataProfileIdToken = settings["MetadataProfileId"];

                var hasValidValue = TryParseMetadataProfileId(metadataProfileIdToken, out var currentValue) &&
                                    currentValue.HasValue &&
                                    profileTypeById.TryGetValue(currentValue.Value, out var currentType) &&
                                    allowedProfileTypes.Contains(currentType);

                if (hasValidValue)
                {
                    return false;
                }

                settings["MetadataProfileId"] = defaultProfileId;
                updatedSettingsJson = settings.ToString(Formatting.None);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadMetadataProfileId(string settingsJson, out int? metadataProfileId)
        {
            metadataProfileId = null;

            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return false;
            }

            try
            {
                var settings = JObject.Parse(settingsJson);
                var token = settings["MetadataProfileId"];
                return TryParseMetadataProfileId(token, out metadataProfileId);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseMetadataProfileId(JToken token, out int? value)
        {
            value = null;

            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return true;
            }

            if (token.Type == JTokenType.Integer)
            {
                var v = token.Value<int>();
                value = v > 0 ? v : null;
                return true;
            }

            if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var parsed) && parsed > 0)
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static bool IsMediaTypeConfigured(string rootFolderPath, int? qualityProfileId, int? monitorExisting, string mediaPath)
        {
            return !string.IsNullOrWhiteSpace(rootFolderPath) ||
                   (qualityProfileId.HasValue && qualityProfileId.Value > 0) ||
                   monitorExisting.HasValue ||
                   !string.IsNullOrWhiteSpace(mediaPath);
        }

        private static int ResolveProfileId(string rootFolderPath, Dictionary<string, int> rootFolderProfiles, int defaultProfileId)
        {
            if (!string.IsNullOrWhiteSpace(rootFolderPath))
            {
                var key = NormalizePathKey(rootFolderPath);
                if (!string.IsNullOrWhiteSpace(key) && rootFolderProfiles.TryGetValue(key, out var configured))
                {
                    return configured;
                }
            }

            return defaultProfileId;
        }

        private static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.TrimEnd('/', '\\');
            return string.IsNullOrWhiteSpace(trimmed) ? path : trimmed;
        }

        private class MetadataProfileRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int ProfileType { get; set; }
        }

        private class RootFolderRow
        {
            public int Id { get; set; }
            public string Path { get; set; }
            public int FolderType { get; set; }
            public string AudiobookSettings { get; set; }
            public string EbookSettings { get; set; }
        }

        private class AuthorRow
        {
            public int Id { get; set; }
            public string AudiobookRootFolderPath { get; set; }
            public string EbookRootFolderPath { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }
            public int? AudiobookQualityProfileId { get; set; }
            public int? EbookQualityProfileId { get; set; }
            public int? AudiobookMonitorExisting { get; set; }
            public int? EbookMonitorExisting { get; set; }
            public string AudiobookPath { get; set; }
            public string EbookPath { get; set; }
        }
    }
}

