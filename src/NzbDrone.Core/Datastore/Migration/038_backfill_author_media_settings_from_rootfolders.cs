using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(38)]
    public class backfill_author_media_settings_from_rootfolders : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var rootFolders = connection.Query<RootFolderRow>(
                    @"SELECT ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings""
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

                    if (TryReadMediaTypeSettings(rootFolder.AudiobookSettings, out var audiobookSettings) && audiobookSettings != null)
                    {
                        audiobookSettingsByPath[normalizedPath] = audiobookSettings;
                    }

                    if (TryReadMediaTypeSettings(rootFolder.EbookSettings, out var ebookSettings) && ebookSettings != null)
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
                             ""EbookMonitorFuture"",
                             ""AudiobookPath"",
                             ""EbookPath""
                      FROM ""Authors"";",
                    transaction: transaction).ToList();

                foreach (var author in authors)
                {
                    var audiobookConfigured = IsMediaTypeConfigured(author.AudiobookRootFolderPath, author.AudiobookQualityProfileId, author.AudiobookMonitorExisting, author.AudiobookPath);
                    var ebookConfigured = IsMediaTypeConfigured(author.EbookRootFolderPath, author.EbookQualityProfileId, author.EbookMonitorExisting, author.EbookPath);

                    int? audiobookQualityProfileId = null;
                    int? audiobookMetadataProfileId = null;
                    int? audiobookMonitorExisting = null;
                    bool? audiobookMonitorFuture = null;

                    int? ebookQualityProfileId = null;
                    int? ebookMetadataProfileId = null;
                    int? ebookMonitorExisting = null;
                    bool? ebookMonitorFuture = null;

                    if (audiobookConfigured)
                    {
                        var needsAudiobookDefaults =
                            !author.AudiobookQualityProfileId.HasValue ||
                            !author.AudiobookMetadataProfileId.HasValue ||
                            !author.AudiobookMonitorExisting.HasValue ||
                            !author.AudiobookMonitorFuture.HasValue;

                        if (needsAudiobookDefaults && TryResolveSettings(author.AudiobookRootFolderPath, audiobookSettingsByPath, out var settings))
                        {
                            if (!author.AudiobookQualityProfileId.HasValue && settings.QualityProfileId.HasValue && settings.QualityProfileId.Value > 0)
                            {
                                audiobookQualityProfileId = settings.QualityProfileId.Value;
                            }

                            if (!author.AudiobookMetadataProfileId.HasValue && settings.MetadataProfileId.HasValue && settings.MetadataProfileId.Value > 0)
                            {
                                audiobookMetadataProfileId = settings.MetadataProfileId.Value;
                            }

                            if (!author.AudiobookMonitorExisting.HasValue && settings.MonitorExisting.HasValue)
                            {
                                audiobookMonitorExisting = settings.MonitorExisting.Value;
                            }

                            if (!author.AudiobookMonitorFuture.HasValue && settings.MonitorFuture.HasValue)
                            {
                                audiobookMonitorFuture = settings.MonitorFuture.Value;
                            }
                        }
                    }

                    if (ebookConfigured)
                    {
                        var needsEbookDefaults =
                            !author.EbookQualityProfileId.HasValue ||
                            !author.EbookMetadataProfileId.HasValue ||
                            !author.EbookMonitorExisting.HasValue ||
                            !author.EbookMonitorFuture.HasValue;

                        if (needsEbookDefaults && TryResolveSettings(author.EbookRootFolderPath, ebookSettingsByPath, out var settings))
                        {
                            if (!author.EbookQualityProfileId.HasValue && settings.QualityProfileId.HasValue && settings.QualityProfileId.Value > 0)
                            {
                                ebookQualityProfileId = settings.QualityProfileId.Value;
                            }

                            if (!author.EbookMetadataProfileId.HasValue && settings.MetadataProfileId.HasValue && settings.MetadataProfileId.Value > 0)
                            {
                                ebookMetadataProfileId = settings.MetadataProfileId.Value;
                            }

                            if (!author.EbookMonitorExisting.HasValue && settings.MonitorExisting.HasValue)
                            {
                                ebookMonitorExisting = settings.MonitorExisting.Value;
                            }

                            if (!author.EbookMonitorFuture.HasValue && settings.MonitorFuture.HasValue)
                            {
                                ebookMonitorFuture = settings.MonitorFuture.Value;
                            }
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
                              SET ""AudiobookQualityProfileId"" = COALESCE(""AudiobookQualityProfileId"", @AudiobookQualityProfileId),
                                  ""AudiobookMetadataProfileId"" = COALESCE(""AudiobookMetadataProfileId"", @AudiobookMetadataProfileId),
                                  ""AudiobookMonitorExisting"" = COALESCE(""AudiobookMonitorExisting"", @AudiobookMonitorExisting),
                                  ""AudiobookMonitorFuture"" = COALESCE(""AudiobookMonitorFuture"", @AudiobookMonitorFuture),
                                  ""EbookQualityProfileId"" = COALESCE(""EbookQualityProfileId"", @EbookQualityProfileId),
                                  ""EbookMetadataProfileId"" = COALESCE(""EbookMetadataProfileId"", @EbookMetadataProfileId),
                                  ""EbookMonitorExisting"" = COALESCE(""EbookMonitorExisting"", @EbookMonitorExisting),
                                  ""EbookMonitorFuture"" = COALESCE(""EbookMonitorFuture"", @EbookMonitorFuture)
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
            });
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
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsMediaTypeConfigured(string rootFolderPath, int? qualityProfileId, int? monitorExisting, string mediaPath)
        {
            return !string.IsNullOrWhiteSpace(rootFolderPath) ||
                   (qualityProfileId.HasValue && qualityProfileId.Value > 0) ||
                   monitorExisting.HasValue ||
                   !string.IsNullOrWhiteSpace(mediaPath);
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

        private class RootFolderRow
        {
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
            public int? AudiobookQualityProfileId { get; set; }
            public int? EbookQualityProfileId { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }
            public int? AudiobookMonitorExisting { get; set; }
            public int? EbookMonitorExisting { get; set; }
            public bool? AudiobookMonitorFuture { get; set; }
            public bool? EbookMonitorFuture { get; set; }
            public string AudiobookPath { get; set; }
            public string EbookPath { get; set; }
        }
    }
}

