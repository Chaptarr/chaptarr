using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;

namespace NzbDrone.Core.Books
{
    public interface IEditionSelector
    {
        Edition SelectBestEdition(IEnumerable<Edition> editions, BookMediaType mediaType);
        EditionRetentionSelection SelectRetainedEditions(
            BookMediaType instanceMediaType,
            IReadOnlyList<Edition> editions);
        bool EnsureSingleMonitoredEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null, BookMediaType? mediaType = null);
    }

    public sealed record EditionRetentionSelection(
        IReadOnlyList<Edition> RetainedEditions,
        IReadOnlyList<string> Warnings);

    public class EditionSelector : IEditionSelector
    {
        private readonly Logger _logger;

        public EditionSelector(Logger logger)
        {
            _logger = logger;
        }

        internal static Edition SelectByNativeFormatThenRatings(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            if (editions == null)
            {
                return null;
            }

            var candidates = editions
                .Where(e => e != null)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
            var nativeFormatCandidates = candidates
                .Where(e => e.ReadingFormatId == nativeFormat)
                .ToList();

            if (nativeFormatCandidates.Any())
            {
                return SelectMostRated(nativeFormatCandidates);
            }

            return SelectRepresentativeFallback(candidates, mediaType);
        }

        public Edition SelectBestEdition(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            if (editions == null || !editions.Any())
            {
                return null;
            }

            var candidates = editions.Where(e => e != null).ToList();
            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
            var nativeCandidates = candidates.Where(e => e.ReadingFormatId == nativeFormat).ToList();

            candidates = nativeCandidates.Any()
                ? nativeCandidates
                : candidates.Where(e => IsRepresentativeFallback(e, mediaType)).ToList();

            if (!candidates.Any())
            {
                _logger.Debug("No native-format or representative fallback editions available for media type {0}", mediaType);
                return null;
            }

            // 1. Manual pin wins within the native media type or representative fallback set.
            var manual = candidates.Where(e => e.ManualAdd).ToList();
            if (manual.Any())
            {
                var selected = manual.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                               ?? manual.OrderBy(e => e.Id).First();
                _logger.Debug("Using manually selected edition: {0} (ID: {1})", selected.Title, selected.Id);
                return selected;
            }

            // Allowed-language filtering happens upstream in EditionMetadataProfileFilter.Apply().
            // Prefer native format (audio=2, ebook=3), then pick most rated with deterministic ties.
            var selected2 = SelectByNativeFormatThenRatings(candidates, mediaType);
            if (selected2 == null)
            {
                return null;
            }

            _logger.Debug("Selected edition '{0}' (ID: {1}, Format: {2}, Votes: {3}, Rating: {4})",
                selected2.Title, selected2.Id, selected2.ReadingFormatId,
                selected2.Ratings?.Votes ?? 0, selected2.Ratings?.Value ?? 0m);

            return selected2;
        }

        /// <summary>
        /// Returns the retained edition set for refresh/add.
        /// Shapes an already-filtered edition set into the retained edition set for add/refresh.
        /// This method does not apply metadata-profile filtering; callers must do that upstream.
        /// </summary>
        public EditionRetentionSelection SelectRetainedEditions(
            BookMediaType instanceMediaType,
            IReadOnlyList<Edition> remoteEditions)
        {
            if (remoteEditions == null || remoteEditions.Count == 0)
            {
                return new EditionRetentionSelection(new List<Edition>(), new List<string>());
            }

            var candidates = remoteEditions
                .Where(e => e != null)
                .ToList();

            var retained = new List<Edition>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nativeFormat = instanceMediaType == BookMediaType.Audiobook ? 2 : 3;

            foreach (var languageGroup in candidates.GroupBy(e => EditionMetadataProfileFilter.NormalizeLanguageBucket(e.Language) ?? "null"))
            {
                var groupEditions = languageGroup.ToList();

                foreach (var edition in groupEditions.Where(e => e.ReadingFormatId == nativeFormat))
                {
                    AddRetainedEdition(retained, seenKeys, edition);
                }

                // Audiobook book rows always retain a non-audio representative per language
                // (best RF=3 ebook, else RF=1 print) so users have an ebook safety net for
                // self-narrated and unprovable-narrator titles even when audio editions exist.
                // Ebook book rows skip the print companion when ebooks are present.
                var hasNative = groupEditions.Any(e => e.ReadingFormatId == nativeFormat);
                if (instanceMediaType == BookMediaType.Audiobook || !hasNative)
                {
                    AddRetainedEdition(retained, seenKeys, SelectRepresentativeFallback(groupEditions, instanceMediaType));
                }
            }

            return new EditionRetentionSelection(retained, new List<string>());
        }

        /// <summary>
        /// Write-side repair selector used only to restore the single monitored-edition invariant.
        /// Read/display paths must use the persisted monitored edition directly.
        /// </summary>
        private static Edition SelectRepairEdition(IEnumerable<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null)
        {
            if (editions == null)
            {
                return null;
            }

            var list = editions.Where(e => e != null).ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var manual = list.Where(e => e.ManualAdd).ToList();
            if (manual.Any())
            {
                return manual.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                       ?? manual.OrderBy(e => e.Id).First();
            }

            if (fileCountsByEditionId != null)
            {
                var withFiles = list
                    .Where(e => fileCountsByEditionId.TryGetValue(e.Id, out var c) && c > 0)
                    .ToList();

                if (withFiles.Any())
                {
                    return withFiles.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                           ?? withFiles.OrderByDescending(e => fileCountsByEditionId.GetValueOrDefault(e.Id, 0))
                                       .ThenBy(e => e.Id).First();
                }
            }

            var monitored = list.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
            if (monitored != null)
            {
                return monitored;
            }

            return list.OrderBy(e => e.Id).First();
        }

        public bool EnsureSingleMonitoredEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null, BookMediaType? mediaType = null)
        {
            if (editions == null)
            {
                return false;
            }

            var list = editions.Where(e => e != null).ToList();
            if (list.Count == 0)
            {
                return false;
            }

            var hasManual = list.Any(e => e.ManualAdd);
            var hasFiles = fileCountsByEditionId != null && list.Any(e => fileCountsByEditionId.TryGetValue(e.Id, out var c) && c > 0);
            var monitoredEditions = list.Where(e => e.Monitored).OrderBy(e => e.Id).ToList();

            Edition selected;
            if (monitoredEditions.Count == 1)
            {
                selected = monitoredEditions[0];
            }
            else if (monitoredEditions.Count == 0 && !hasManual && !hasFiles && mediaType.HasValue)
            {
                selected = SelectByNativeFormatThenRatings(list, mediaType.Value);
            }
            else
            {
                selected = SelectRepairEdition(list, fileCountsByEditionId);
            }

            if (selected == null)
            {
                return false;
            }

            var changed = false;
            foreach (var edition in list)
            {
                var shouldMonitor = edition.Id == selected.Id;
                if (edition.Monitored != shouldMonitor)
                {
                    edition.Monitored = shouldMonitor;
                    changed = true;
                }
            }

            if (changed)
            {
                _logger.Info("[EditionRepair] BookId={0} chosenEditionId={1}", selected.BookId, selected.Id);
            }

            return changed;
        }

        internal static string GetRetentionDedupeKey(Edition edition)
        {
            if (edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"foreign:{edition.ForeignEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.HardcoverEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"hardcover:{edition.HardcoverEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.GoodreadsEditionId > 0)
            {
                return $"goodreads:{edition.GoodreadsEditionId}";
            }

            if (edition?.OpenLibraryEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"openlibrary:{edition.OpenLibraryEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.GoogleBooksEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"google:{edition.GoogleBooksEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.Asin.IsNotNullOrWhiteSpace() == true)
            {
                return $"asin:{edition.Asin.Trim().ToLowerInvariant()}";
            }

            if (edition?.AudibleASIN.IsNotNullOrWhiteSpace() == true)
            {
                return $"audible:{edition.AudibleASIN.Trim().ToLowerInvariant()}";
            }

            var normalizedTitle = edition?.Title?.Trim().ToLowerInvariant() ?? string.Empty;
            var normalizedLanguage = edition?.Language?.CanonicalizeLanguage()?.Trim().ToLowerInvariant() ?? "null";
            var readingFormat = edition?.ReadingFormatId ?? 0;
            var releaseDate = edition?.ReleaseDate?.ToString("yyyyMMdd") ?? "null";

            return $"fallback:{normalizedTitle}|{normalizedLanguage}|{readingFormat}|{releaseDate}";
        }

        private static void AddRetainedEdition(List<Edition> retained, HashSet<string> seenKeys, Edition edition)
        {
            if (edition == null)
            {
                return;
            }

            var key = GetRetentionDedupeKey(edition);
            if (seenKeys.Add(key))
            {
                retained.Add(edition);
            }
        }

        private static bool IsRepresentativeFallback(Edition edition, BookMediaType mediaType)
        {
            return GetRepresentativeFallbackRank(edition, mediaType) > 0;
        }

        private static int GetRepresentativeFallbackRank(Edition edition, BookMediaType mediaType)
        {
            if (edition == null)
            {
                return 0;
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                return edition.ReadingFormatId switch
                {
                    3 => 2,
                    1 => 1,
                    _ => 0
                };
            }

            return edition.ReadingFormatId == 1 ? 1 : 0;
        }

        private static Edition SelectRepresentativeFallback(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            return (editions ?? Enumerable.Empty<Edition>())
                .Where(e => IsRepresentativeFallback(e, mediaType))
                .OrderByDescending(e => GetRepresentativeFallbackRank(e, mediaType))
                .ThenByDescending(e => e.Ratings?.Votes ?? 0)
                .ThenByDescending(e => e.Ratings?.Value ?? 0m)
                .ThenBy(e => e.Id)
                .FirstOrDefault();
        }

        private static Edition SelectMostRated(IEnumerable<Edition> editions)
        {
            return (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .OrderByDescending(e => e.Ratings?.Votes ?? 0)
                .ThenByDescending(e => e.Ratings?.Value ?? 0m)
                .ThenBy(e => e.Id)
                .FirstOrDefault();
        }

    }
}
