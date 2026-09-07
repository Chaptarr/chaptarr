using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    internal sealed class DirectDownloadAdapterResult
    {
        public DirectDownloadAdapterResult(bool supported, IReadOnlyList<ReleaseInfo> releases)
        {
            Supported = supported;
            Releases = releases ?? Array.Empty<ReleaseInfo>();
        }

        public bool Supported { get; }

        public IReadOnlyList<ReleaseInfo> Releases { get; }
    }

    internal static class DirectDownloadSearchTerms
    {
        public const int MaxSearchCandidates = 10;
        public static IReadOnlyList<string> Build(DirectDownloadProbeRequest request)
        {
            var terms = new List<string>();
            Add(terms, NormalizeIsbn(request?.Isbn));
            Add(terms, request?.Title);
            return terms;
        }

        public static string NormalizeIsbn(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return null;
            }

            var characters = isbn
                .Where(character => char.IsDigit(character) || character == 'x' || character == 'X')
                .Select(character => char.ToUpperInvariant(character))
                .ToArray();

            return characters.Length == 0 ? null : new string(characters);
        }

        private static void Add(List<string> terms, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var trimmed = candidate.Trim();
            if (!terms.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                terms.Add(trimmed);
            }
        }
    }

    internal static class DirectDownloadReleaseFactory
    {
        public static readonly ISet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "epub", "mobi", "azw3", "pdf", "djvu", "cbz", "cbr", "fb2", "txt"
        };

        public static ReleaseInfo Create(
            string author,
            string bookTitle,
            string extension,
            long size,
            DateTime publishDate,
            string isbn,
            string infoUrl,
            string downloadUrl,
            DirectDownloadSourceFamily family)
        {
            var normalizedIsbn = DirectDownloadSearchTerms.NormalizeIsbn(isbn);
            var normalizedTitle = string.IsNullOrWhiteSpace(author)
                ? $"{bookTitle} [{extension}]"
                : $"{author} - {bookTitle} [{extension}]";
            var stableKey = $"{family}|{downloadUrl}|{infoUrl}|{normalizedTitle}|{normalizedIsbn}";

            return new ReleaseInfo
            {
                Guid = $"Direct-{family}-{stableKey.SHA256Hash().Substring(0, 24)}",
                Title = normalizedTitle,
                Author = author,
                Book = bookTitle,
                Isbn = normalizedIsbn,
                Size = size,
                PublishDate = publishDate,
                InfoUrl = infoUrl,
                DownloadUrl = downloadUrl,
                CommentUrl = infoUrl,
                Container = extension,
                DownloadProtocol = DownloadProtocol.Direct,
                Source = family.ToString()
            };
        }
    }
}
