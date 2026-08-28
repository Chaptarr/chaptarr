using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Datastore.Migration
{
    internal static class AuthorMediaSettingsBackfillRepair
    {
        private const string NoneProfileName = "None";
        private const string AudiobookDefaultProfileName = "Audiobook Default";
        private const string EbookDefaultProfileName = "Ebook Default";

        public static void Apply(IDbConnection connection, IDbTransaction transaction)
        {
            var metadataProfiles = connection.Query<MetadataProfileRow>(
                @"SELECT ""Id"", ""Name"", ""ProfileType"" FROM ""MetadataProfiles"";",
                transaction: transaction).ToList();

            var metadataProfileTypeById = metadataProfiles.ToDictionary(x => x.Id, x => x.ProfileType);
            var audiobookDefaultMetadataProfileId = GetDefaultMetadataProfileId(metadataProfiles, MetadataProfileType.Audiobook, AudiobookDefaultProfileName);
            var ebookDefaultMetadataProfileId = GetDefaultMetadataProfileId(metadataProfiles, MetadataProfileType.Ebook, EbookDefaultProfileName);

            var qualityProfileTypesById = connection.Query<QualityProfileRow>(
                @"SELECT ""Id"", ""ProfileType"" FROM ""QualityProfiles"";",
                transaction: transaction).ToDictionary(x => x.Id, x => x.ProfileType);

            var rootFolders = connection.Query<RootFolderRow>(
                @"SELECT ""Id"", ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings""
                  FROM ""RootFolders"";",
                transaction: transaction).ToList();

            var audiobookSettingsByPath = new Dictionary<string, MediaTypeSettings>(StringComparer.OrdinalIgnoreCase);
            var ebookSettingsByPath = new Dictionary<string, MediaTypeSettings>(StringComparer.OrdinalIgnoreCase);

            foreach (var rootFolder in rootFolders)
            {
                var normalizedPath = NormalizePathKey(rootFolder.Path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                var supportsAudiobook = rootFolder.FolderType == FolderType.Mixed || rootFolder.FolderType == FolderType.Audiobook;
                var supportsEbook = rootFolder.FolderType == FolderType.Mixed || rootFolder.FolderType == FolderType.Ebook;

                if (supportsAudiobook &&
                    TryEnsureMetadataProfileId(rootFolder.AudiobookSettings, audiobookDefaultMetadataProfileId, MetadataProfileType.General, MetadataProfileType.Audiobook, metadataProfileTypeById, out var repairedAudiobookSettings))
                {
                    rootFolder.AudiobookSettings = repairedAudiobookSettings;
                    connection.Execute(
                        @"UPDATE ""RootFolders"" SET ""AudiobookSettings"" = @AudiobookSettings WHERE ""Id"" = @Id;",
                        new { rootFolder.AudiobookSettings, rootFolder.Id },
                        transaction: transaction);
                }

                if (supportsEbook &&
                    TryEnsureMetadataProfileId(rootFolder.EbookSettings, ebookDefaultMetadataProfileId, MetadataProfileType.General, MetadataProfileType.Ebook, metadataProfileTypeById, out var repairedEbookSettings))
                {
                    rootFolder.EbookSettings = repairedEbookSettings;
                    connection.Execute(
                        @"UPDATE ""RootFolders"" SET ""EbookSettings"" = @EbookSettings WHERE ""Id"" = @Id;",
                        new { rootFolder.EbookSettings, rootFolder.Id },
                        transaction: transaction);
                }

                if (supportsAudiobook && TryReadMediaTypeSettings(rootFolder.AudiobookSettings, out var audiobookSettings) && audiobookSettings != null)
                {
                    audiobookSettingsByPath[normalizedPath] = audiobookSettings;
                }

                if (supportsEbook && TryReadMediaTypeSettings(rootFolder.EbookSettings, out var ebookSettings) && ebookSettings != null)
                {
                    ebookSettingsByPath[normalizedPath] = ebookSettings;
                }
            }

            var authors = connection.Query<AuthorRow>(
                @"SELECT ""Id"",
                         ""AudiobookRootFolderPath"",
                         ""EbookRootFolderPath"",
                         ""AudiobookQualityProfileId"",
                         ""EbookQualityProfileId"",
                         ""AudiobookMetadataProfileId"",
                         ""EbookMetadataProfileId"",
                         ""AudiobookMonitorExisting"",
                         ""EbookMonitorExisting"",
                         ""AudiobookMonitorFuture"",
                         ""EbookMonitorFuture""
                  FROM ""Authors"";",
                transaction: transaction).ToList();

            foreach (var author in authors)
            {
                int? audiobookQualityProfileId = null;
                int? audiobookMetadataProfileId = null;
                int? audiobookMonitorExisting = null;
                bool? audiobookMonitorFuture = null;

                int? ebookQualityProfileId = null;
                int? ebookMetadataProfileId = null;
                int? ebookMonitorExisting = null;
                bool? ebookMonitorFuture = null;

                if (TryResolveSettings(author.AudiobookRootFolderPath, audiobookSettingsByPath, out var audiobookSettings))
                {
                    if (!IsValidQualityProfileId(author.AudiobookQualityProfileId, ProfileType.Audiobook, qualityProfileTypesById) &&
                        IsValidQualityProfileId(audiobookSettings.QualityProfileId, ProfileType.Audiobook, qualityProfileTypesById))
                    {
                        audiobookQualityProfileId = audiobookSettings.QualityProfileId.Value;
                    }

                    if (!IsValidMetadataProfileId(author.AudiobookMetadataProfileId, metadataProfileTypeById, MetadataProfileType.General, MetadataProfileType.Audiobook))
                    {
                        if (IsValidMetadataProfileId(audiobookSettings.MetadataProfileId, metadataProfileTypeById, MetadataProfileType.General, MetadataProfileType.Audiobook))
                        {
                            audiobookMetadataProfileId = audiobookSettings.MetadataProfileId.Value;
                        }
                        else if (audiobookDefaultMetadataProfileId.HasValue)
                        {
                            audiobookMetadataProfileId = audiobookDefaultMetadataProfileId.Value;
                        }
                    }

                    if (!author.AudiobookMonitorExisting.HasValue && audiobookSettings.MonitorExisting.HasValue)
                    {
                        audiobookMonitorExisting = audiobookSettings.MonitorExisting.Value;
                    }

                    if (!author.AudiobookMonitorFuture.HasValue && audiobookSettings.MonitorFuture.HasValue)
                    {
                        audiobookMonitorFuture = audiobookSettings.MonitorFuture.Value;
                    }
                }

                if (TryResolveSettings(author.EbookRootFolderPath, ebookSettingsByPath, out var ebookSettings))
                {
                    if (!IsValidQualityProfileId(author.EbookQualityProfileId, ProfileType.Ebook, qualityProfileTypesById) &&
                        IsValidQualityProfileId(ebookSettings.QualityProfileId, ProfileType.Ebook, qualityProfileTypesById))
                    {
                        ebookQualityProfileId = ebookSettings.QualityProfileId.Value;
                    }

                    if (!IsValidMetadataProfileId(author.EbookMetadataProfileId, metadataProfileTypeById, MetadataProfileType.General, MetadataProfileType.Ebook))
                    {
                        if (IsValidMetadataProfileId(ebookSettings.MetadataProfileId, metadataProfileTypeById, MetadataProfileType.General, MetadataProfileType.Ebook))
                        {
                            ebookMetadataProfileId = ebookSettings.MetadataProfileId.Value;
                        }
                        else if (ebookDefaultMetadataProfileId.HasValue)
                        {
                            ebookMetadataProfileId = ebookDefaultMetadataProfileId.Value;
                        }
                    }

                    if (!author.EbookMonitorExisting.HasValue && ebookSettings.MonitorExisting.HasValue)
                    {
                        ebookMonitorExisting = ebookSettings.MonitorExisting.Value;
                    }

                    if (!author.EbookMonitorFuture.HasValue && ebookSettings.MonitorFuture.HasValue)
                    {
                        ebookMonitorFuture = ebookSettings.MonitorFuture.Value;
                    }
                }

                if (audiobookQualityProfileId.HasValue ||
                    audiobookMetadataProfileId.HasValue ||
                    audiobookMonitorExisting.HasValue ||
                    audiobookMonitorFuture.HasValue ||
                    ebookQualityProfileId.HasValue ||
                    ebookMetadataProfileId.HasValue ||
                    ebookMonitorExisting.HasValue ||
                    ebookMonitorFuture.HasValue)
                {
                    connection.Execute(
                        @"UPDATE ""Authors""
                          SET ""AudiobookQualityProfileId"" = COALESCE(@AudiobookQualityProfileId, ""AudiobookQualityProfileId""),
                              ""AudiobookMetadataProfileId"" = COALESCE(@AudiobookMetadataProfileId, ""AudiobookMetadataProfileId""),
                              ""AudiobookMonitorExisting"" = COALESCE(@AudiobookMonitorExisting, ""AudiobookMonitorExisting""),
                              ""AudiobookMonitorFuture"" = COALESCE(@AudiobookMonitorFuture, ""AudiobookMonitorFuture""),
                              ""EbookQualityProfileId"" = COALESCE(@EbookQualityProfileId, ""EbookQualityProfileId""),
                              ""EbookMetadataProfileId"" = COALESCE(@EbookMetadataProfileId, ""EbookMetadataProfileId""),
                              ""EbookMonitorExisting"" = COALESCE(@EbookMonitorExisting, ""EbookMonitorExisting""),
                              ""EbookMonitorFuture"" = COALESCE(@EbookMonitorFuture, ""EbookMonitorFuture"")
                          WHERE ""Id"" = @Id;",
                        new
                        {
                            author.Id,
                            AudiobookQualityProfileId = audiobookQualityProfileId,
                            AudiobookMetadataProfileId = audiobookMetadataProfileId,
                            AudiobookMonitorExisting = audiobookMonitorExisting,
                            AudiobookMonitorFuture = audiobookMonitorFuture,
                            EbookQualityProfileId = ebookQualityProfileId,
                            EbookMetadataProfileId = ebookMetadataProfileId,
                            EbookMonitorExisting = ebookMonitorExisting,
                            EbookMonitorFuture = ebookMonitorFuture
                        },
                        transaction: transaction);
                }
            }
        }

        private static int? GetDefaultMetadataProfileId(List<MetadataProfileRow> profiles, MetadataProfileType profileType, string preferredName)
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

        private static bool IsValidQualityProfileId(int? profileId, ProfileType expectedType, Dictionary<int, ProfileType> qualityProfileTypesById)
        {
            return profileId.HasValue &&
                   profileId.Value > 0 &&
                   qualityProfileTypesById.TryGetValue(profileId.Value, out var profileType) &&
                   profileType == expectedType;
        }

        private static bool IsValidMetadataProfileId(int? profileId, Dictionary<int, MetadataProfileType> metadataProfileTypeById, params MetadataProfileType[] allowedProfileTypes)
        {
            return profileId.HasValue &&
                   profileId.Value > 0 &&
                   metadataProfileTypeById.TryGetValue(profileId.Value, out var profileType) &&
                   allowedProfileTypes.Contains(profileType);
        }

        private static bool TryEnsureMetadataProfileId(string settingsJson,
                                                       int? defaultProfileId,
                                                       MetadataProfileType allowedProfileType1,
                                                       MetadataProfileType allowedProfileType2,
                                                       Dictionary<int, MetadataProfileType> metadataProfileTypeById,
                                                       out string updatedSettingsJson)
        {
            updatedSettingsJson = settingsJson;

            if (!defaultProfileId.HasValue || string.IsNullOrWhiteSpace(settingsJson))
            {
                return false;
            }

            try
            {
                var settings = JObject.Parse(settingsJson);
                var token = settings["MetadataProfileId"];
                var hasValidValue = TryParsePositiveInt(token, out var metadataProfileId) &&
                                    metadataProfileId.HasValue &&
                                    IsValidMetadataProfileId(metadataProfileId, metadataProfileTypeById, allowedProfileType1, allowedProfileType2);

                if (hasValidValue)
                {
                    return false;
                }

                settings["MetadataProfileId"] = defaultProfileId.Value;
                updatedSettingsJson = settings.ToString(Formatting.None);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryResolveSettings(string rootFolderPath, Dictionary<string, MediaTypeSettings> settingsByRootFolderPath, out MediaTypeSettings settings)
        {
            settings = null;

            if (string.IsNullOrWhiteSpace(rootFolderPath))
            {
                return false;
            }

            var key = NormalizePathKey(rootFolderPath);
            return !string.IsNullOrWhiteSpace(key) && settingsByRootFolderPath.TryGetValue(key, out settings);
        }

        private static bool TryReadMediaTypeSettings(string settingsJson, out MediaTypeSettings settings)
        {
            settings = null;

            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return false;
            }

            try
            {
                settings = JsonConvert.DeserializeObject<MediaTypeSettings>(settingsJson);
                // The 061 repair runs before migration 104 and therefore still reads
                // legacy root JSON. MediaTypeSettings intentionally ignores those
                // compatibility properties when serializing the new shape, so recover
                // the historical values explicitly for this pre-104 repair only.
                var payload = JObject.Parse(settingsJson);
                var legacyExisting = payload.TryGetValue("MonitorExisting", StringComparison.OrdinalIgnoreCase, out var existingToken) &&
                                     existingToken.Type != JTokenType.Null
                    ? existingToken.Value<int?>()
                    : null;
                var legacyFuture = payload.TryGetValue("MonitorFuture", StringComparison.OrdinalIgnoreCase, out var futureToken) &&
                                   futureToken.Type != JTokenType.Null
                    ? futureToken.Value<bool?>()
                    : null;
                settings.ApplyLegacyMonitoringSettings(legacyExisting, legacyFuture);
                settings.SetLegacyCompatibilityValues(legacyExisting, legacyFuture);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParsePositiveInt(JToken token, out int? value)
        {
            value = null;

            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return true;
            }

            if (token.Type == JTokenType.Integer)
            {
                var parsed = token.Value<int>();
                value = parsed > 0 ? parsed : null;
                return true;
            }

            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out var fromString))
            {
                value = fromString > 0 ? fromString : null;
                return true;
            }

            return false;
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
            public MetadataProfileType ProfileType { get; set; }
        }

        private class QualityProfileRow
        {
            public int Id { get; set; }
            public ProfileType ProfileType { get; set; }
        }

        private class RootFolderRow
        {
            public int Id { get; set; }
            public string Path { get; set; }
            public FolderType FolderType { get; set; }
            public string AudiobookSettings { get; set; }
            public string EbookSettings { get; set; }
        }

        private class AuthorRow
        {
            public int Id { get; set; }
            public string AudiobookRootFolderPath { get; set; }
            public string EbookRootFolderPath { get; set; }
            public int? AudiobookQualityProfileId { get; set; }
            public int? EbookQualityProfileId { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }
            public int? AudiobookMonitorExisting { get; set; }
            public int? EbookMonitorExisting { get; set; }
            public bool? AudiobookMonitorFuture { get; set; }
            public bool? EbookMonitorFuture { get; set; }
        }
    }
}
