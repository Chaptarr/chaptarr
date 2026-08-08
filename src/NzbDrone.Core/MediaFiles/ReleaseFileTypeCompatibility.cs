using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles
{
    public static class ReleaseFileTypeCompatibility
    {
        private static readonly Regex FileTypeTokenRegex = new Regex(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReleaseTitleExtensionRegex = new Regex(@"\.(?<extension>[a-z0-9]+)(?![\p{L}\p{Nd}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> SupportedFileTypeTokens = new HashSet<string>(
            MediaFileExtensions.AllExtensions.Select(e => e.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SupportedTextFileTypeTokens = new HashSet<string>(
            MediaFileExtensions.TextExtensions.Select(e => e.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SupportedAudioFileTypeTokens = new HashSet<string>(
            MediaFileExtensions.AudioExtensions.Select(e => e.TrimStart('.')),
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> KnownUnsupportedFileTypeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cbr",
            "cbz",
            "djvu",
            "fb2",
            "lit",
            "pdb",
            "txt",
            "kfx",
            "mkv",
            "avi",
            "mov",
            "wmv",
            "flv",
            "webm",
            "doc",
            "docx",
            "rtf",
            "html",
            "htm"
        };

        public static bool TryGetKnownUnsupportedFileType(string fileType, out string unsupportedFileType)
        {
            unsupportedFileType = null;

            var tokens = Tokenize(fileType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!tokens.Any())
            {
                return false;
            }

            if (tokens.Any(SupportedFileTypeTokens.Contains))
            {
                return false;
            }

            var unsupportedTokens = tokens.Where(KnownUnsupportedFileTypeTokens.Contains).ToList();
            if (!unsupportedTokens.Any())
            {
                return false;
            }

            unsupportedFileType = string.Join(", ", unsupportedTokens);
            return true;
        }

        public static bool TryGetKnownUnsupportedReleaseTitleFileType(string releaseTitle, out string unsupportedFileType)
        {
            unsupportedFileType = null;

            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return false;
            }

            var tokens = ReleaseTitleExtensionRegex
                .Matches(releaseTitle)
                .Cast<Match>()
                .Select(match => match.Groups["extension"].Value)
                .Select(extension => extension.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            tokens.AddRange(Tokenize(releaseTitle)
                .Where(token => SupportedFileTypeTokens.Contains(token))
                .Select(token => token.ToLowerInvariant()));

            tokens = tokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Title scanning is a fallback for indexers that omit structured FileType.
            // If the title advertises an importable payload, sidecar-looking tokens
            // like readme.txt should not make the whole release look unsupported.
            if (tokens.Any(SupportedFileTypeTokens.Contains))
            {
                return false;
            }

            var unsupportedTokens = tokens
                .Where(extension => KnownUnsupportedFileTypeTokens.Contains(extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!unsupportedTokens.Any())
            {
                return false;
            }

            unsupportedFileType = string.Join(", ", unsupportedTokens);
            return true;
        }

        public static bool TryGetMediaTypeMismatch(string fileType, BookMediaType requestedMediaType, out string mismatchedFileType)
        {
            mismatchedFileType = null;

            var tokens = Tokenize(fileType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!tokens.Any())
            {
                return false;
            }

            var supportedAudioTokens = tokens.Where(SupportedAudioFileTypeTokens.Contains).ToList();
            var supportedTextTokens = tokens.Where(SupportedTextFileTypeTokens.Contains).ToList();

            if (!supportedAudioTokens.Any() && !supportedTextTokens.Any())
            {
                return false;
            }

            if (requestedMediaType == BookMediaType.Audiobook && !supportedAudioTokens.Any() && supportedTextTokens.Any())
            {
                mismatchedFileType = string.Join(", ", supportedTextTokens);
                return true;
            }

            if (requestedMediaType == BookMediaType.Ebook && !supportedTextTokens.Any() && supportedAudioTokens.Any())
            {
                mismatchedFileType = string.Join(", ", supportedAudioTokens);
                return true;
            }

            return false;
        }

        private static IEnumerable<string> Tokenize(string fileType)
        {
            if (string.IsNullOrWhiteSpace(fileType))
            {
                yield break;
            }

            foreach (Match match in FileTypeTokenRegex.Matches(fileType))
            {
                if (match.Success && !string.IsNullOrWhiteSpace(match.Value))
                {
                    yield return match.Value.ToLowerInvariant();
                }
            }
        }
    }
}
