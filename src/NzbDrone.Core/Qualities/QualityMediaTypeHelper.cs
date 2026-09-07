using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Qualities
{
    public static class QualityMediaTypeHelper
    {
        private static readonly Regex AudiobookTitleHintRegex = new(@"(?:^|[\s\[\]()_.-])(audiobook|audio\s*book|audible|graphic\s*audio|full\s*cast|mp3|m4b|m4a|mka|flac)(?:$|[\s\[\]()_.-])",
                                                                    RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EbookTitleHintRegex = new(@"(?:^|[\s\[\]()_.-])(ebook|e-book|epub|kepub|mobi|azw3?|pdf)(?:$|[\s\[\]()_.-])",
                                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            if (quality != Quality.Unknown && quality != Quality.UnknownAudio)
            {
                return null;
            }

            if (LooksLikeAudiobook(release))
            {
                return BookMediaType.Audiobook;
            }

            if (LooksLikeEbook(release))
            {
                return BookMediaType.Ebook;
            }

            return null;
        }

        public static BookMediaType? DetectMediaType(Quality quality, string title)
        {
            var knownMediaType = GetKnownMediaType(quality);
            if (knownMediaType.HasValue)
            {
                return knownMediaType;
            }

            if (quality != Quality.Unknown && quality != Quality.UnknownAudio)
            {
                return null;
            }

            if (AudiobookTitleHintRegex.IsMatch(title ?? string.Empty))
            {
                return BookMediaType.Audiobook;
            }

            if (EbookTitleHintRegex.IsMatch(title ?? string.Empty))
            {
                return BookMediaType.Ebook;
            }

            return null;
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

        private static bool LooksLikeAudiobook(ReleaseInfo release)
        {
            var torrent = release as TorrentInfo;
            var fileType = torrent?.FileType ?? release?.Container;
            if (!string.IsNullOrWhiteSpace(fileType))
            {
                var parts = fileType.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => AudioExtensions.Contains(part)))
                {
                    return true;
                }

                if (parts.Any(part => EbookExtensions.Contains(part)))
                {
                    return false;
                }
            }

            return AudiobookTitleHintRegex.IsMatch(release?.Title ?? string.Empty);
        }

        private static bool LooksLikeEbook(ReleaseInfo release)
        {
            var torrent = release as TorrentInfo;
            var fileType = torrent?.FileType ?? release?.Container;
            if (!string.IsNullOrWhiteSpace(fileType))
            {
                var parts = fileType.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => EbookExtensions.Contains(part)))
                {
                    return true;
                }

                if (parts.Any(part => AudioExtensions.Contains(part)))
                {
                    return false;
                }
            }

            return EbookTitleHintRegex.IsMatch(release?.Title ?? string.Empty);
        }
    }
}
