using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public static class MediaFileExtensions
    {
        public const string MatroskaAudioExtension = ".mka";

        private static readonly Dictionary<string, Quality> _textExtensions;
        private static readonly Dictionary<string, Quality> _audioExtensions;
        private static readonly HashSet<string> _textExtensionSet;
        private static readonly HashSet<string> _audioExtensionSet;
        private static readonly HashSet<string> _allExtensionSet;
        private static readonly HashSet<string> _singleFileBookContainerSet;

        // Formats Chaptarr can write series metadata into without help from Calibre. Both are
        // OPF-in-a-zip; the Mobi-derived formats and PDF have no writer.
        private static readonly HashSet<string> _seriesTagWritableExtensionSet =
            new HashSet<string>(new[] { ".epub", ".kepub" }, StringComparer.OrdinalIgnoreCase);

        static MediaFileExtensions()
        {
            _textExtensions = new Dictionary<string, Quality>(StringComparer.OrdinalIgnoreCase)
            {
                { ".epub", Quality.EPUB },
                { ".kepub", Quality.EPUB },
                { ".mobi", Quality.MOBI },
                { ".azw3", Quality.AZW3 },
                { ".azw", Quality.AZW3 },
                { ".pdf", Quality.PDF },
            };

            _audioExtensions = new Dictionary<string, Quality>(StringComparer.OrdinalIgnoreCase)
            {
                { ".flac", Quality.FLAC },
                { ".ape", Quality.FLAC },
                { ".wavpack", Quality.FLAC },
                { ".wav", Quality.FLAC },
                { ".alac", Quality.FLAC },
                { ".mp2", Quality.MP3 },
                { ".mp3", Quality.MP3 },
                { ".wma", Quality.MP3 },
                { ".m4a", Quality.MP3 },
                { ".m4p", Quality.MP3 },
                { ".m4b", Quality.M4B },
                { ".mp4", Quality.MP3 },
                { ".aac", Quality.MP3 },
                { ".mp4a", Quality.MP3 },
                { ".ogg", Quality.MP3 },
                { ".oga", Quality.MP3 },
                { ".opus", Quality.MP3 },
                { ".vorbis", Quality.MP3 },
                { MatroskaAudioExtension, Quality.UnknownAudio },
            };

            _textExtensionSet = new HashSet<string>(_textExtensions.Keys, StringComparer.OrdinalIgnoreCase);
            _audioExtensionSet = new HashSet<string>(_audioExtensions.Keys, StringComparer.OrdinalIgnoreCase);
            _allExtensionSet = new HashSet<string>(_textExtensions.Keys.Concat(_audioExtensions.Keys), StringComparer.OrdinalIgnoreCase);
            _singleFileBookContainerSet = new HashSet<string>(_textExtensions.Keys, StringComparer.OrdinalIgnoreCase);
        }

        public static IReadOnlySet<string> TextExtensions => _textExtensionSet;
        public static IReadOnlySet<string> AudioExtensions => _audioExtensionSet;
        public static IReadOnlySet<string> AllExtensions => _allExtensionSet;

        public static Quality GetQualityForExtension(string extension)
        {
            if (_textExtensions.ContainsKey(extension))
            {
                return _textExtensions[extension];
            }

            if (_audioExtensions.ContainsKey(extension))
            {
                return _audioExtensions[extension];
            }

            return Quality.Unknown;
        }

        public static Quality GetQualityForExtension(string extension, MediaInfoModel mediaInfo)
        {
            if (IsMatroskaAudioExtension(extension))
            {
                return GetMatroskaAudioQuality(mediaInfo);
            }

            return GetQualityForExtension(extension);
        }

        public static bool IsMatroskaAudioExtension(string extension)
        {
            return string.Equals(extension, MatroskaAudioExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanWriteAudioTags(string extension)
        {
            return AudioExtensions.Contains(extension) && !IsMatroskaAudioExtension(extension);
        }

        public static bool CanWriteEbookSeriesTags(string extension)
        {
            return extension != null && _seriesTagWritableExtensionSet.Contains(extension);
        }

        public static bool IsSingleFileBookContainer(string extension)
        {
            return _singleFileBookContainerSet.Contains(extension);
        }

        private static Quality GetMatroskaAudioQuality(MediaInfoModel mediaInfo)
        {
            var audioFormat = mediaInfo?.AudioFormat;
            if (string.IsNullOrWhiteSpace(audioFormat))
            {
                return Quality.UnknownAudio;
            }

            var normalized = audioFormat.ToLowerInvariant();
            if (normalized.Contains("flac") ||
                normalized.Contains("alac") ||
                normalized.Contains("wavpack") ||
                normalized.Contains("pcm") ||
                normalized.Contains("truehd") ||
                normalized.Contains("mlp") ||
                normalized.Contains("ape") ||
                normalized.Contains("monkey"))
            {
                return Quality.FLAC;
            }

            return Quality.MP3;
        }
    }
}
