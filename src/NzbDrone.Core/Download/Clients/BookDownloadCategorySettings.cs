using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Download.Clients
{
    public interface IBookDownloadCategorySettings
    {
        string MusicCategory { get; }
        string EbookCategory { get; }
        string AudiobookCategory { get; }
    }

    public static class BookDownloadCategorySettings
    {
        public static string GetCategory(IBookDownloadCategorySettings settings, BookMediaType mediaType)
        {
            if (settings == null)
            {
                return null;
            }

            return mediaType == BookMediaType.Ebook
                ? FirstConfigured(settings.EbookCategory, settings.MusicCategory)
                : FirstConfigured(settings.AudiobookCategory, settings.MusicCategory);
        }

        public static IReadOnlyList<string> GetConfiguredCategories(IBookDownloadCategorySettings settings)
        {
            if (settings == null)
            {
                return Array.Empty<string>();
            }

            var fallbackCategory = string.IsNullOrWhiteSpace(settings.EbookCategory) || string.IsNullOrWhiteSpace(settings.AudiobookCategory)
                ? new[] { settings.MusicCategory }
                : Array.Empty<string>();

            return fallbackCategory.Concat(new[] { settings.EbookCategory, settings.AudiobookCategory })
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool MatchesConfiguredCategory(IBookDownloadCategorySettings settings, string category)
        {
            var configured = GetConfiguredCategories(settings);

            return configured.Count == 0
                ? string.IsNullOrWhiteSpace(category) || category == "*"
                : configured.Contains(category, StringComparer.OrdinalIgnoreCase);
        }

        public static string FindCategoryInPath(IBookDownloadCategorySettings settings, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            return GetConfiguredCategories(settings)
                .FirstOrDefault(category => segments.Any(segment => string.Equals(segment, category, StringComparison.OrdinalIgnoreCase)));
        }

        public static string GetCategoryFieldName(IBookDownloadCategorySettings settings, string category)
        {
            if (settings == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(category) && string.Equals(settings.EbookCategory?.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "EbookCategory";
            }

            if (!string.IsNullOrWhiteSpace(category) && string.Equals(settings.AudiobookCategory?.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "AudiobookCategory";
            }

            return "MusicCategory";
        }

        private static string FirstConfigured(string preferred, string fallback)
        {
            return !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;
        }
    }
}
