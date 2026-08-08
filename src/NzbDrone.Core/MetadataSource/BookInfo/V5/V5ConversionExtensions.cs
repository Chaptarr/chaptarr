using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    public static class V5ConversionExtensions
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        // JSON size monitoring constants
        private const int MAX_PROVIDER_URLS_SIZE = 10240; // 10KB limit
        private const int WARN_PROVIDER_URLS_SIZE = 8192; // 8KB warning threshold

        private static readonly JsonSerializerOptions ProviderUrlsSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private static readonly string[] ProviderUrlsPreferredKeys =
        {
            "goodreads",
            "hardcover",
            "amazon",
            "audible",
            "openlibrary",
            "googlebooks"
        };

        // Author ID conversions
        public static long? GetGoodreadsIdAsLong(this V5AuthorData author)
        {
            if (string.IsNullOrEmpty(author.GoodreadsAuthorId)) return null;
            if (long.TryParse(author.GoodreadsAuthorId, out var id)) return id;
            _logger.Warn($"Invalid Goodreads Author ID format: {author.GoodreadsAuthorId}");
            return null;
        }

        // Book ID conversions
        public static long? GetGoodreadsBookIdAsLong(this V5Book book)
        {
            if (string.IsNullOrEmpty(book.GoodreadsBookId)) return null;
            if (long.TryParse(book.GoodreadsBookId, out var id)) return id;
            _logger.Warn($"Invalid Goodreads Book ID format: {book.GoodreadsBookId}");
            return null;
        }

        public static long? GetGoodreadsWorkIdAsLong(this V5Book book)
        {
            if (string.IsNullOrEmpty(book.GoodreadsWorkId)) return null;
            if (long.TryParse(book.GoodreadsWorkId, out var id)) return id;
            _logger.Warn($"Invalid Goodreads Work ID format: {book.GoodreadsWorkId}");
            return null;
        }

        // Series ID conversions
        public static long? GetGoodreadsSeriesIdAsLong(this V5Series series)
        {
            if (string.IsNullOrEmpty(series.GoodreadsSeriesId)) return null;
            if (long.TryParse(series.GoodreadsSeriesId, out var id)) return id;
            _logger.Warn($"Invalid Goodreads Series ID format: {series.GoodreadsSeriesId}");
            return null;
        }

        // Edition ID conversions
        public static long? GetGoodreadsEditionIdAsLong(this V5Edition edition)
        {
            if (string.IsNullOrEmpty(edition.GoodreadsEditionId)) return null;
            if (long.TryParse(edition.GoodreadsEditionId, out var id)) return id;
            _logger.Warn($"Invalid Goodreads Edition ID format: {edition.GoodreadsEditionId}");
            return null;
        }

        // DateTime conversion with timezone handling
        public static DateTime? ToUtcDateTime(this DateTime? dateTime)
        {
            if (!dateTime.HasValue) return null;
            return dateTime.Value.Kind == DateTimeKind.Utc
                ? dateTime.Value
                : dateTime.Value.ToUniversalTime();
        }

        // DateTime parsing from ISO 8601 string
        public static DateTime? ToUtcDateTime(this string dateTimeString)
        {
            if (string.IsNullOrEmpty(dateTimeString)) return null;

            if (DateTime.TryParse(dateTimeString, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            }

            _logger.Warn($"Invalid DateTime format: {dateTimeString}");
            return null;
        }

        // Provider URLs normalization and size validation.
        // ProviderUrls is URL-only in Chaptarr (provider -> absolute URL string). Any non-HTTP(S) values are dropped.
        public static ProviderUrlMap ValidateProviderUrls(this IDictionary<string, string> urls)
        {
            var normalized = new ProviderUrlMap();

            if (urls != null && urls.Count > 0)
            {
                foreach (var kvp in urls)
                {
                    var key = kvp.Key?.Trim();
                    var url = kvp.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    if (!url.IsValidHttpUrl())
                    {
                        continue;
                    }

                    normalized.SetNormalized(key, url);
                }
            }

            var sizeBytes = JsonSerializer.SerializeToUtf8Bytes(normalized, ProviderUrlsSerializerOptions).Length;

            if (sizeBytes > MAX_PROVIDER_URLS_SIZE)
            {
                _logger.Warn($"ProviderUrls exceeds size limit: {sizeBytes} bytes, trimming...");
                return TrimProviderUrls(normalized);
            }

            if (sizeBytes > WARN_PROVIDER_URLS_SIZE)
            {
                _logger.Debug($"ProviderUrls approaching size limit: {sizeBytes} bytes");
            }

            return normalized;
        }

        private static ProviderUrlMap TrimProviderUrls(ProviderUrlMap urls)
        {
            if (urls == null || urls.Count == 0)
            {
                return new ProviderUrlMap();
            }

            var orderedKeys = new List<string>();

            // Prefer common “primary” links first, then fall back to the remaining keys deterministically.
            foreach (var key in ProviderUrlsPreferredKeys)
            {
                if (key.IsNotNullOrWhiteSpace() && urls.ContainsKey(key))
                {
                    orderedKeys.Add(key);
                }
            }

            foreach (var key in urls.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (!orderedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    orderedKeys.Add(key);
                }
            }

            var trimmed = new ProviderUrlMap();

            foreach (var key in orderedKeys)
            {
                if (!urls.TryGetValue(key, out var value) || value.IsNullOrWhiteSpace())
                {
                    continue;
                }

                trimmed.SetNormalized(key, value);

                var sizeBytes = JsonSerializer.SerializeToUtf8Bytes(trimmed, ProviderUrlsSerializerOptions).Length;
                if (sizeBytes > MAX_PROVIDER_URLS_SIZE)
                {
                    trimmed.Remove(key);
                    break;
                }
            }

            return trimmed;
        }

        // Performance monitoring helper
        public static void LogPerformance(string operation, Action action)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > 100)
            {
                _logger.Warn($"{operation} took {stopwatch.ElapsedMilliseconds}ms");
            }
        }

        // Genres validation
        public static List<string> ValidateGenres(this List<string> genres, string bookTitle = null)
        {
            if (genres == null) return new List<string>();

            if (genres.Count > 50)
            {
                _logger.Warn($"Book {bookTitle ?? "Unknown"} has {genres.Count} genres, truncating to 50...");
                return genres.GetRange(0, 50);
            }

            return genres;
        }

        // Pseudonyms validation
        public static List<string> ValidatePseudonyms(this List<string> pseudonyms, string authorName = null)
        {
            if (pseudonyms == null) return new List<string>();

            if (pseudonyms.Count > 20)
            {
                _logger.Warn($"Author {authorName ?? "Unknown"} has {pseudonyms.Count} pseudonyms, truncating to 20...");
                return pseudonyms.GetRange(0, 20);
            }

            return pseudonyms;
        }
    }
}
