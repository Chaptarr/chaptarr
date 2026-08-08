using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(63)]
    public class dedupe_author_provider_ids_and_enforce_uniqueness : NzbDroneMigrationBase
    {
        private class AuthorRow
        {
            public int Id { get; set; }
            public string Name { get; set; }

            public string HardcoverAuthorId { get; set; }
            public string GoodreadsAuthorId { get; set; }
            public string AudnexusAuthorId { get; set; }
            public string OpenLibraryAuthorId { get; set; }
            public string GoogleBooksAuthorId { get; set; }

            public string AudiobookRootFolderPath { get; set; }
            public string EbookRootFolderPath { get; set; }
            public string AudiobookPath { get; set; }
            public string EbookPath { get; set; }

            public int? AudiobookQualityProfileId { get; set; }
            public int? EbookQualityProfileId { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }

            public int? AudiobookMonitorExisting { get; set; }
            public bool? AudiobookMonitorFuture { get; set; }
            public int? EbookMonitorExisting { get; set; }
            public bool? EbookMonitorFuture { get; set; }

            public bool AudiobookSettingsManuallyOverridden { get; set; }
            public bool EbookSettingsManuallyOverridden { get; set; }

            public string Tags { get; set; }
        }

        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Authors").Exists())
            {
                return;
            }

            var hasSyncMetadata = Schema.Table("AuthorSyncMetadata").Exists();
            var hasAuthorNormalized = Schema.Table("author_normalized").Exists();

            Execute.WithConnection((connection, transaction) =>
            {
                var authors = connection.Query<AuthorRow>(
                    @"SELECT ""Id"", ""Name"",
                             ""HardcoverAuthorId"", ""GoodreadsAuthorId"", ""AudnexusAuthorId"", ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId"",
                             ""AudiobookRootFolderPath"", ""EbookRootFolderPath"", ""AudiobookPath"", ""EbookPath"",
                             ""AudiobookQualityProfileId"", ""EbookQualityProfileId"", ""AudiobookMetadataProfileId"", ""EbookMetadataProfileId"",
                             ""AudiobookMonitorExisting"", ""AudiobookMonitorFuture"", ""EbookMonitorExisting"", ""EbookMonitorFuture"",
                             ""AudiobookSettingsManuallyOverridden"", ""EbookSettingsManuallyOverridden"",
                             ""Tags""
                      FROM ""Authors"";",
                    transaction: transaction).ToList();

                if (authors.Count == 0)
                {
                    return;
                }

                var bookCounts = connection.Query<(int AuthorId, int Count)>(
                        @"SELECT ""AuthorId"" AS AuthorId, COUNT(1) AS Count
                          FROM ""Books""
                          GROUP BY ""AuthorId"";",
                        transaction: transaction)
                    .ToDictionary(x => x.AuthorId, x => x.Count);

                var syncMetadataOwner = hasSyncMetadata
                    ? connection.Query<(int AuthorId, int Id)>(
                            @"SELECT ""AuthorId"" AS AuthorId, ""Id"" AS Id
                              FROM ""AuthorSyncMetadata"";",
                            transaction: transaction)
                        .ToDictionary(x => x.AuthorId, x => x.Id)
                    : new Dictionary<int, int>();

                // 1) Normalize provider IDs in-place (trim, collapse double-prefixing, add default prefixes when missing)
                // Also repair obvious cross-field placement (e.g. HardcoverAuthorId accidentally containing a gr: id).
                var normalizedCount = 0;
                foreach (var author in authors)
                {
                    var before = SnapshotProviderIds(author);
                    NormalizeAndRepairProviderIds(author);

                    if (!ProviderIdsEqual(before, author))
                    {
                        normalizedCount += connection.Execute(
                            @"UPDATE ""Authors""
                              SET ""HardcoverAuthorId"" = @HardcoverAuthorId,
                                  ""GoodreadsAuthorId"" = @GoodreadsAuthorId,
                                  ""AudnexusAuthorId"" = @AudnexusAuthorId,
                                  ""OpenLibraryAuthorId"" = @OpenLibraryAuthorId,
                                  ""GoogleBooksAuthorId"" = @GoogleBooksAuthorId
                              WHERE ""Id"" = @Id;",
                            new
                            {
                                author.HardcoverAuthorId,
                                author.GoodreadsAuthorId,
                                author.AudnexusAuthorId,
                                author.OpenLibraryAuthorId,
                                author.GoogleBooksAuthorId,
                                author.Id
                            },
                            transaction: transaction);
                    }
                }

                if (normalizedCount > 0)
                {
                    _logger.Info("[MIGRATION-63] Normalized provider IDs for {0} author row(s)", normalizedCount);
                }

                // 2) Deduplicate authors by provider IDs (one author per external identity)
                var parent = authors.ToDictionary(a => a.Id, a => a.Id);

                int Find(int x)
                {
                    while (parent[x] != x)
                    {
                        parent[x] = parent[parent[x]];
                        x = parent[x];
                    }

                    return x;
                }

                void Union(int a, int b)
                {
                    var ra = Find(a);
                    var rb = Find(b);
                    if (ra != rb)
                    {
                        parent[rb] = ra;
                    }
                }

                var firstByProviderId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in authors)
                {
                    foreach (var pid in EnumerateProviderIds(a))
                    {
                        if (firstByProviderId.TryGetValue(pid, out var first))
                        {
                            Union(first, a.Id);
                        }
                        else
                        {
                            firstByProviderId[pid] = a.Id;
                        }
                    }
                }

                var groups = authors.GroupBy(a => Find(a.Id)).Where(g => g.Count() > 1).ToList();

                var dedupedAuthors = 0;
                if (groups.Count > 0)
                {
                    foreach (var group in groups)
                    {
                    var members = group.ToList();

                    // Prefer the author with more books, then with more provider IDs, then the lowest Id.
                    var survivor = members
                        .OrderByDescending(a => bookCounts.TryGetValue(a.Id, out var c) ? c : 0)
                        .ThenByDescending(CountProviderIds)
                        .ThenBy(a => a.Id)
                        .First();

                        foreach (var dupe in members.Where(m => m.Id != survivor.Id).OrderBy(m => m.Id))
                        {
                        MergeInto(survivor, dupe);

                        // Reassign dependent rows to the survivor
                        ReassignAuthorId(connection, transaction, table: "Books", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "AuthorSeries", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "MetadataFiles", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "OtherFiles", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "DownloadHistory", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "ExtraFiles", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "Blacklist", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "History", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                        ReassignAuthorId(connection, transaction, table: "PendingReleases", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);
                            ReassignAuthorId(connection, transaction, table: "author_trigrams", column: "AuthorId", fromAuthorId: dupe.Id, toAuthorId: survivor.Id);

                        // author_normalized uses AuthorId as PK; delete and let it be regenerated later
                        if (hasAuthorNormalized)
                        {
                            connection.Execute(
                                @"DELETE FROM ""author_normalized"" WHERE ""AuthorId"" = @AuthorId;",
                                new { AuthorId = dupe.Id },
                                transaction: transaction);
                        }

                        // Preserve sync metadata ownership when possible (avoid losing ETag/state on delete)
                        if (hasSyncMetadata &&
                            syncMetadataOwner.TryGetValue(dupe.Id, out var syncId) &&
                            !syncMetadataOwner.ContainsKey(survivor.Id))
                        {
                            try
                            {
                                connection.Execute(
                                    @"UPDATE ""AuthorSyncMetadata"" SET ""AuthorId"" = @ToAuthorId WHERE ""Id"" = @Id;",
                                    new { ToAuthorId = survivor.Id, Id = syncId },
                                    transaction: transaction);
                                syncMetadataOwner[survivor.Id] = syncId;
                                syncMetadataOwner.Remove(dupe.Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[MIGRATION-63] Failed to reassign AuthorSyncMetadata Id={0} from AuthorId={1} to AuthorId={2}",
                                    syncId, dupe.Id, survivor.Id);
                            }
                        }

                        // Finally delete the duplicate author row
                        connection.Execute(
                            @"DELETE FROM ""Authors"" WHERE ""Id"" = @Id;",
                            new { dupe.Id },
                            transaction: transaction);

                        dedupedAuthors++;
                        }

                    // Persist merged fields on the survivor (only the fields we touched)
                        connection.Execute(
                            @"UPDATE ""Authors""
                          SET ""HardcoverAuthorId"" = @HardcoverAuthorId,
                              ""GoodreadsAuthorId"" = @GoodreadsAuthorId,
                              ""AudnexusAuthorId"" = @AudnexusAuthorId,
                              ""OpenLibraryAuthorId"" = @OpenLibraryAuthorId,
                              ""GoogleBooksAuthorId"" = @GoogleBooksAuthorId,
                              ""AudiobookRootFolderPath"" = @AudiobookRootFolderPath,
                              ""EbookRootFolderPath"" = @EbookRootFolderPath,
                              ""AudiobookPath"" = @AudiobookPath,
                              ""EbookPath"" = @EbookPath,
                              ""AudiobookQualityProfileId"" = @AudiobookQualityProfileId,
                              ""EbookQualityProfileId"" = @EbookQualityProfileId,
                              ""AudiobookMetadataProfileId"" = @AudiobookMetadataProfileId,
                              ""EbookMetadataProfileId"" = @EbookMetadataProfileId,
                              ""AudiobookMonitorExisting"" = @AudiobookMonitorExisting,
                              ""AudiobookMonitorFuture"" = @AudiobookMonitorFuture,
                              ""EbookMonitorExisting"" = @EbookMonitorExisting,
                              ""EbookMonitorFuture"" = @EbookMonitorFuture,
                              ""AudiobookSettingsManuallyOverridden"" = @AudiobookSettingsManuallyOverridden,
                              ""EbookSettingsManuallyOverridden"" = @EbookSettingsManuallyOverridden,
                              ""Tags"" = @Tags
                          WHERE ""Id"" = @Id;",
                            survivor,
                            transaction: transaction);
                    }
                }

                if (dedupedAuthors > 0)
                {
                    _logger.Warn("[MIGRATION-63] Deduplicated {0} duplicate author row(s) by provider ID", dedupedAuthors);
                }
            });

            // 3) Enforce uniqueness going forward (prevents race-condition duplicates, multi-instance duplicates, and bypass paths)
            // Replace the old non-unique Hardcover index with a unique one.
            if (Schema.Table("Authors").Index("IX_Authors_HardcoverAuthorId").Exists())
            {
                Delete.Index("IX_Authors_HardcoverAuthorId").OnTable("Authors");
            }

            if (!Schema.Table("Authors").Index("UX_Authors_HardcoverAuthorId").Exists())
            {
                Create.Index("UX_Authors_HardcoverAuthorId").OnTable("Authors").OnColumn("HardcoverAuthorId").Unique();
            }

            if (!Schema.Table("Authors").Index("UX_Authors_GoodreadsAuthorId").Exists())
            {
                Create.Index("UX_Authors_GoodreadsAuthorId").OnTable("Authors").OnColumn("GoodreadsAuthorId").Unique();
            }

            if (!Schema.Table("Authors").Index("UX_Authors_AudnexusAuthorId").Exists())
            {
                Create.Index("UX_Authors_AudnexusAuthorId").OnTable("Authors").OnColumn("AudnexusAuthorId").Unique();
            }

            if (!Schema.Table("Authors").Index("UX_Authors_OpenLibraryAuthorId").Exists())
            {
                Create.Index("UX_Authors_OpenLibraryAuthorId").OnTable("Authors").OnColumn("OpenLibraryAuthorId").Unique();
            }

            if (!Schema.Table("Authors").Index("UX_Authors_GoogleBooksAuthorId").Exists())
            {
                Create.Index("UX_Authors_GoogleBooksAuthorId").OnTable("Authors").OnColumn("GoogleBooksAuthorId").Unique();
            }
        }

        private static (string Hardcover, string Goodreads, string Audnexus, string OpenLibrary, string GoogleBooks) SnapshotProviderIds(AuthorRow a)
        {
            return (a.HardcoverAuthorId, a.GoodreadsAuthorId, a.AudnexusAuthorId, a.OpenLibraryAuthorId, a.GoogleBooksAuthorId);
        }

        private static bool ProviderIdsEqual((string Hardcover, string Goodreads, string Audnexus, string OpenLibrary, string GoogleBooks) before, AuthorRow after)
        {
            return string.Equals(before.Hardcover, after.HardcoverAuthorId, StringComparison.Ordinal) &&
                   string.Equals(before.Goodreads, after.GoodreadsAuthorId, StringComparison.Ordinal) &&
                   string.Equals(before.Audnexus, after.AudnexusAuthorId, StringComparison.Ordinal) &&
                   string.Equals(before.OpenLibrary, after.OpenLibraryAuthorId, StringComparison.Ordinal) &&
                   string.Equals(before.GoogleBooks, after.GoogleBooksAuthorId, StringComparison.Ordinal);
        }

        private static IEnumerable<string> EnumerateProviderIds(AuthorRow a)
        {
            if (!string.IsNullOrWhiteSpace(a.HardcoverAuthorId)) yield return a.HardcoverAuthorId.Trim();
            if (!string.IsNullOrWhiteSpace(a.GoodreadsAuthorId)) yield return a.GoodreadsAuthorId.Trim();
            if (!string.IsNullOrWhiteSpace(a.AudnexusAuthorId)) yield return a.AudnexusAuthorId.Trim();
            if (!string.IsNullOrWhiteSpace(a.OpenLibraryAuthorId)) yield return a.OpenLibraryAuthorId.Trim();
            if (!string.IsNullOrWhiteSpace(a.GoogleBooksAuthorId)) yield return a.GoogleBooksAuthorId.Trim();
        }

        private static int CountProviderIds(AuthorRow a)
        {
            var count = 0;
            if (!string.IsNullOrWhiteSpace(a.HardcoverAuthorId)) count++;
            if (!string.IsNullOrWhiteSpace(a.GoodreadsAuthorId)) count++;
            if (!string.IsNullOrWhiteSpace(a.AudnexusAuthorId)) count++;
            if (!string.IsNullOrWhiteSpace(a.OpenLibraryAuthorId)) count++;
            if (!string.IsNullOrWhiteSpace(a.GoogleBooksAuthorId)) count++;
            return count;
        }

	        private static void NormalizeAndRepairProviderIds(AuthorRow author)
	        {
	            // Trim blanks to null
	            author.HardcoverAuthorId = NullIfWhiteSpace(author.HardcoverAuthorId);
	            author.GoodreadsAuthorId = NullIfWhiteSpace(author.GoodreadsAuthorId);
	            author.AudnexusAuthorId = NullIfWhiteSpace(author.AudnexusAuthorId);
	            author.OpenLibraryAuthorId = NullIfWhiteSpace(author.OpenLibraryAuthorId);
	            author.GoogleBooksAuthorId = NullIfWhiteSpace(author.GoogleBooksAuthorId);

	            // Move obviously mis-filed prefixed IDs into the correct column when possible.
	            var hardcoverAuthorId = author.HardcoverAuthorId;
	            MoveIfPrefixed(ref hardcoverAuthorId, "hc", (p, v) => AssignByPrefix(author, p, v));
	            author.HardcoverAuthorId = hardcoverAuthorId;

	            var goodreadsAuthorId = author.GoodreadsAuthorId;
	            MoveIfPrefixed(ref goodreadsAuthorId, "gr", (p, v) => AssignByPrefix(author, p, v));
	            author.GoodreadsAuthorId = goodreadsAuthorId;

	            var openLibraryAuthorId = author.OpenLibraryAuthorId;
	            MoveIfPrefixed(ref openLibraryAuthorId, "ol", (p, v) => AssignByPrefix(author, p, v));
	            author.OpenLibraryAuthorId = openLibraryAuthorId;

	            var googleBooksAuthorId = author.GoogleBooksAuthorId;
	            MoveIfPrefixed(ref googleBooksAuthorId, "gb", (p, v) => AssignByPrefix(author, p, v));
	            author.GoogleBooksAuthorId = googleBooksAuthorId;
	            RepairAudnexusPrefixed(author);

	            // Normalize/collapse accidental double-prefixing and add default prefixes when missing.
	            author.HardcoverAuthorId = NormalizeFixedPrefix(author.HardcoverAuthorId, "hc");
	            author.GoodreadsAuthorId = NormalizeFixedPrefix(author.GoodreadsAuthorId, "gr");
            author.OpenLibraryAuthorId = NormalizeFixedPrefix(author.OpenLibraryAuthorId, "ol");
            author.GoogleBooksAuthorId = NormalizeFixedPrefix(author.GoogleBooksAuthorId, "gb");
            author.AudnexusAuthorId = NormalizeAudnexus(author.AudnexusAuthorId);
        }

	        private static void RepairAudnexusPrefixed(AuthorRow author)
	        {
	            if (author == null)
	            {
	                return;
	            }

	            var audnexusAuthorId = author.AudnexusAuthorId;
	            MoveIfPrefixed(ref audnexusAuthorId, "az", (p, v) => AssignByPrefix(author, p, v));
	            author.AudnexusAuthorId = audnexusAuthorId;
	        }

        private static void AssignByPrefix(AuthorRow author, string prefix, string value)
        {
            if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            prefix = prefix.Trim().ToLowerInvariant();

            switch (prefix)
            {
                case "hc":
                    author.HardcoverAuthorId ??= value;
                    break;
                case "gr":
                    author.GoodreadsAuthorId ??= value;
                    break;
                case "ol":
                    author.OpenLibraryAuthorId ??= value;
                    break;
                case "gb":
                    author.GoogleBooksAuthorId ??= value;
                    break;
                case "az":
                case "an":
                    author.AudnexusAuthorId ??= value;
                    break;
            }
        }

        private static void MoveIfPrefixed(ref string value, string expectedPrefix, Action<string, string> moveTo)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx >= trimmed.Length - 1)
            {
                value = trimmed;
                return;
            }

            var prefix = trimmed.Substring(0, idx).Trim().ToLowerInvariant();
            var id = trimmed.Substring(idx + 1).Trim();

            // Unwrap nested/double-prefixed values by repeatedly taking the last recognized prefix.
            while (true)
            {
                var innerColon = id.IndexOf(':');
                if (innerColon <= 0 || innerColon >= id.Length - 1)
                {
                    break;
                }

                var innerPrefix = id.Substring(0, innerColon).Trim().ToLowerInvariant();
                var innerId = id.Substring(innerColon + 1).Trim();

                if (innerPrefix is "az" or "an" or "audible" or "amazon" or "kindle" or "audnexus" or "asin" or
                    "hc" or "hardcover" or "gr" or "goodreads" or "ol" or "openlibrary" or "gb" or "googlebooks")
                {
                    prefix = innerPrefix;
                    id = innerId;
                    continue;
                }

                break;
            }

            prefix = prefix switch
            {
                "hardcover" => "hc",
                "goodreads" => "gr",
                "openlibrary" => "ol",
                "googlebooks" => "gb",
                "an" => "az",
                "audnexus" => "az",
                "audible" => "az",
                "amazon" => "az",
                "kindle" => "az",
                "asin" => "az",
                _ => prefix
            };

            expectedPrefix = expectedPrefix?.Trim().ToLowerInvariant();
            expectedPrefix = expectedPrefix switch
            {
                "hardcover" => "hc",
                "goodreads" => "gr",
                "openlibrary" => "ol",
                "googlebooks" => "gb",
                "an" => "az",
                _ => expectedPrefix
            };

            if (string.Equals(prefix, expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = ProviderIdHelper.Normalize($"{expectedPrefix}:{id}", expectedPrefix);
                return;
            }

            // If it looks like a real provider prefix, relocate it.
            if (prefix is "hc" or "gr" or "ol" or "gb" or "az")
            {
                moveTo(prefix, ProviderIdHelper.Normalize($"{prefix}:{id}", prefix));
                value = null;
            }
            else
            {
                value = trimmed;
            }
        }

	        private static string NormalizeFixedPrefix(string providerId, string defaultPrefix)
	        {
	            if (string.IsNullOrWhiteSpace(providerId))
	            {
	                return null;
	            }

	            providerId = providerId.Trim();

	            if (!providerId.Contains(":"))
	            {
	                return ProviderIdHelper.WithPrefix(defaultPrefix, providerId);
	            }

	            // Canonicalize known long-form prefixes into the short prefixes we store in the DB.
	            var idx = providerId.IndexOf(':');
	            if (idx > 0 && idx < providerId.Length - 1)
	            {
	                var prefix = providerId.Substring(0, idx).Trim().ToLowerInvariant();
	                var id = providerId.Substring(idx + 1).Trim();

	                prefix = prefix switch
	                {
	                    "hardcover" => "hc",
	                    "goodreads" => "gr",
	                    "openlibrary" => "ol",
	                    "googlebooks" => "gb",
	                    "an" => "az",
	                    _ => prefix
	                };

	                providerId = $"{prefix}:{id}";
	            }

	            return ProviderIdHelper.Normalize(providerId, defaultPrefix);
	        }

        private static string NormalizeAudnexus(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            providerId = providerId.Trim();

            if (providerId.StartsWith("az:", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderIdHelper.Normalize(providerId, "az");
            }

            if (providerId.StartsWith("an:", StringComparison.OrdinalIgnoreCase) ||
                providerId.StartsWith("audnexus:", StringComparison.OrdinalIgnoreCase) ||
                providerId.StartsWith("audible:", StringComparison.OrdinalIgnoreCase) ||
                providerId.StartsWith("amazon:", StringComparison.OrdinalIgnoreCase) ||
                providerId.StartsWith("kindle:", StringComparison.OrdinalIgnoreCase) ||
                providerId.StartsWith("asin:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = providerId.IndexOf(':');
                var id = idx > 0 && idx < providerId.Length - 1 ? providerId.Substring(idx + 1).Trim() : string.Empty;
                return ProviderIdHelper.Normalize($"az:{id}", "az");
            }

            // Unknown prefix: preserve as-is (never strip/guess across providers).
            if (providerId.Contains(":"))
            {
                return providerId;
            }

            // Raw/unprefixed: treat as Amazon author id.
            return ProviderIdHelper.Normalize($"az:{providerId}", "az");
        }

        private static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static void MergeInto(AuthorRow survivor, AuthorRow dupe)
        {
            survivor.HardcoverAuthorId ??= dupe.HardcoverAuthorId;
            survivor.GoodreadsAuthorId ??= dupe.GoodreadsAuthorId;
            survivor.AudnexusAuthorId ??= dupe.AudnexusAuthorId;
            survivor.OpenLibraryAuthorId ??= dupe.OpenLibraryAuthorId;
            survivor.GoogleBooksAuthorId ??= dupe.GoogleBooksAuthorId;

            survivor.AudiobookRootFolderPath ??= dupe.AudiobookRootFolderPath;
            survivor.EbookRootFolderPath ??= dupe.EbookRootFolderPath;
            survivor.AudiobookPath ??= dupe.AudiobookPath;
            survivor.EbookPath ??= dupe.EbookPath;

            survivor.AudiobookQualityProfileId ??= dupe.AudiobookQualityProfileId;
            survivor.EbookQualityProfileId ??= dupe.EbookQualityProfileId;
            survivor.AudiobookMetadataProfileId ??= dupe.AudiobookMetadataProfileId;
            survivor.EbookMetadataProfileId ??= dupe.EbookMetadataProfileId;

            survivor.AudiobookMonitorExisting ??= dupe.AudiobookMonitorExisting;
            survivor.AudiobookMonitorFuture ??= dupe.AudiobookMonitorFuture;
            survivor.EbookMonitorExisting ??= dupe.EbookMonitorExisting;
            survivor.EbookMonitorFuture ??= dupe.EbookMonitorFuture;

            survivor.AudiobookSettingsManuallyOverridden |= dupe.AudiobookSettingsManuallyOverridden;
            survivor.EbookSettingsManuallyOverridden |= dupe.EbookSettingsManuallyOverridden;

            survivor.Tags = MergeTagsJson(survivor.Tags, dupe.Tags);
        }

        private static string MergeTagsJson(string left, string right)
        {
            left = NullIfWhiteSpace(left);
            right = NullIfWhiteSpace(right);

            if (left == null)
            {
                return right;
            }

            if (right == null)
            {
                return left;
            }

            try
            {
                var l = JsonSerializer.Deserialize<List<int>>(left) ?? new List<int>();
                var r = JsonSerializer.Deserialize<List<int>>(right) ?? new List<int>();
                var merged = l.Concat(r).Distinct().ToList();
                return JsonSerializer.Serialize(merged);
            }
            catch
            {
                // Fail safe: preserve the survivor value.
                return left;
            }
        }

        private static void ReassignAuthorId(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, string table, string column, int fromAuthorId, int toAuthorId)
        {
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column) || fromAuthorId == toAuthorId)
            {
                return;
            }

            connection.Execute(
                $@"UPDATE ""{table}""
                   SET ""{column}"" = @ToAuthorId
                   WHERE ""{column}"" = @FromAuthorId;",
                new { FromAuthorId = fromAuthorId, ToAuthorId = toAuthorId },
                transaction: transaction);
        }
    }
}
