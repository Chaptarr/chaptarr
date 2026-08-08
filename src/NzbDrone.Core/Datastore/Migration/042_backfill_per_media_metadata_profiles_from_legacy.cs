using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(42)]
    public class backfill_per_media_metadata_profiles_from_legacy : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                // Backfill per-media-type metadata profiles from legacy Authors.MetadataProfileId.
                //
                // Rationale:
                // - Chaptarr now treats a missing per-media-type metadata profile (NULL/<=0) as "media type disabled".
                // - Older installs may still only have the legacy MetadataProfileId populated.
                // - Without this backfill, those authors would be interpreted as disabled and could be pruned on refresh.
                //
                // Only backfill when the media type appears configured for the author (root folder/path/quality/monitoring).
                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""AudiobookMetadataProfileId"" = CASE
                              WHEN (""AudiobookMetadataProfileId"" IS NULL OR ""AudiobookMetadataProfileId"" <= 0)
                                   AND ""MetadataProfileId"" IS NOT NULL AND ""MetadataProfileId"" > 0
                                   AND (
                                       COALESCE(""AudiobookRootFolderPath"", '') <> '' OR
                                       COALESCE(""AudiobookPath"", '') <> '' OR
                                       COALESCE(""AudiobookQualityProfileId"", 0) > 0 OR
                                       ""AudiobookMonitorExisting"" IS NOT NULL
                                   )
                              THEN ""MetadataProfileId""
                              ELSE ""AudiobookMetadataProfileId""
                          END,
                          ""EbookMetadataProfileId"" = CASE
                              WHEN (""EbookMetadataProfileId"" IS NULL OR ""EbookMetadataProfileId"" <= 0)
                                   AND ""MetadataProfileId"" IS NOT NULL AND ""MetadataProfileId"" > 0
                                   AND (
                                       COALESCE(""EbookRootFolderPath"", '') <> '' OR
                                       COALESCE(""EbookPath"", '') <> '' OR
                                       COALESCE(""EbookQualityProfileId"", 0) > 0 OR
                                       ""EbookMonitorExisting"" IS NOT NULL
                                   )
                              THEN ""MetadataProfileId""
                              ELSE ""EbookMetadataProfileId""
                          END
                      WHERE ""MetadataProfileId"" IS NOT NULL AND ""MetadataProfileId"" > 0;",
                    transaction: transaction);

                // Safety net: legacy-only authors (MetadataProfileId set, but no per-type profiles) should not be
                // interpreted as "both media types disabled" after the dual-instance architecture change.
                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""AudiobookMetadataProfileId"" = ""MetadataProfileId"",
                          ""EbookMetadataProfileId"" = ""MetadataProfileId""
                      WHERE ""MetadataProfileId"" IS NOT NULL AND ""MetadataProfileId"" > 0
                        AND (""AudiobookMetadataProfileId"" IS NULL OR ""AudiobookMetadataProfileId"" <= 0)
                        AND (""EbookMetadataProfileId"" IS NULL OR ""EbookMetadataProfileId"" <= 0);",
                    transaction: transaction);
            });
        }
    }
}
