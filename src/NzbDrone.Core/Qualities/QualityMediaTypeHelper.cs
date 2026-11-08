using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Qualities
{
    public static class QualityMediaTypeHelper
    {
        private static readonly HashSet<string> AudioExtensions = new(
            MediaFileExtensions.AudioExtensions.Select(extension => extension.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> EbookExtensions = new(
            MediaFileExtensions.TextExtensions.Select(extension => extension.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        public static BookMediaType? GetKnownMediaType(Quality quality)
        {
            if (IsEbookQuality(quality))
            {
                return BookMediaType.Ebook;
            }

            if (IsAudiobookQuality(quality))
            {
                return BookMediaType.Audiobook;
            }

            return null;
        }

        public static BookMediaType? DetectMediaType(Quality quality, ReleaseInfo release)
        {
            var knownMediaType = GetKnownMediaType(quality);
            if (knownMediaType.HasValue)
            {
                return knownMediaType;
            }

            var fileTypes = (release as TorrentInfo)?.FileType?.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var hasAudio = fileTypes?.Any(AudioExtensions.Contains) == true;
            var hasEbooks = fileTypes?.Any(EbookExtensions.Contains) == true;
            if (hasAudio || hasEbooks)
            {
                return hasAudio == hasEbooks ? null : hasAudio ? BookMediaType.Audiobook : BookMediaType.Ebook;
            }

            return GetCategoryMediaType(release);
        }

        public static BookMediaType? GetCategoryMediaType(ReleaseInfo release)
        {
            // Native MAM parent categories are separate from its genre IDs and Newznab categories.
            return (release as TorrentInfo)?.MainCategory switch
            {
                13 => BookMediaType.Audiobook,
                14 => BookMediaType.Ebook,
                _ => SearchMediaTypeHelper.GetMediaTypeFromCategories(release?.Categories)
            };
        }

        public static BookMediaType? GetMediaTypeFromPath(string path)
        {
            string extension;

            try
            {
                extension = Path.GetExtension(path ?? string.Empty)?.TrimStart('.');
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            if (AudioExtensions.Contains(extension))
            {
                return BookMediaType.Audiobook;
            }

            if (EbookExtensions.Contains(extension))
            {
                return BookMediaType.Ebook;
            }

            return null;
        }

        public static bool IsAudiobookQuality(Quality quality)
        {
            return quality == Quality.MP3 ||
                   quality == Quality.M4B ||
                   quality == Quality.FLAC ||
                   quality == Quality.UnknownAudio;
        }

        public static bool IsEbookQuality(Quality quality)
        {
            return quality == Quality.PDF ||
                   quality == Quality.MOBI ||
                   quality == Quality.EPUB ||
                   quality == Quality.AZW3;
        }

        public static bool IsEbookFileQuality(Quality quality)
        {
            return quality == Quality.Unknown || IsEbookQuality(quality);
        }

    }
}
