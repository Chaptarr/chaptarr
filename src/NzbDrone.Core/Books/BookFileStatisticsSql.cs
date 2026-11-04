using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Books
{
    internal static class BookFileStatisticsSql
    {
        private static readonly string _ebookExtensionCondition =
            BuildExtensionCondition(MediaFileExtensions.TextExtensions, "\"BookFiles\".\"Path\"");
        private static readonly string _audiobookExtensionCondition =
            BuildExtensionCondition(MediaFileExtensions.AudioExtensions, "\"BookFiles\".\"Path\"");

        internal static string EbookExtensionCondition => _ebookExtensionCondition;
        internal static string AudiobookExtensionCondition => _audiobookExtensionCondition;

        // Keep the common catalog query lean; format classification is used only by author
        // statistics.
        internal const string GroupedByBook = @"
            SELECT ""Editions"".""BookId"" AS ""BookId"",
                   SUM(""BookFiles"".""Size"") AS ""SizeOnDisk"",
                   COUNT(""BookFiles"".""Id"") AS ""BookFileCount""
            FROM ""BookFiles""
            CROSS JOIN ""Editions""
            WHERE ""Editions"".""Id"" = ""BookFiles"".""EditionId""
            GROUP BY ""Editions"".""BookId""
        ";

        internal static readonly string GroupedByBookWithFormatStatistics = BuildGroupedByBookWithFormatStatistics();

        // Same aggregate restricted to one author's books, so a single-author lookup does not
        // aggregate every file in the library. The id is an int, so inlining it is injection-safe.
        internal static string GroupedByBookWithFormatStatisticsForAuthor(int authorId) =>
            BuildGroupedByBookWithFormatStatistics(authorId);

        private static string BuildGroupedByBookWithFormatStatistics(int? authorId = null)
        {
            var authorFilter = authorId.HasValue
                ? $"AND \"Editions\".\"BookId\" IN " +
                  $"(SELECT \"Id\" FROM \"Books\" WHERE \"AuthorId\" = {authorId.Value})"
                : string.Empty;

            return $@"
            SELECT ""Editions"".""BookId"" AS ""BookId"",
                   SUM(""BookFiles"".""Size"") AS ""SizeOnDisk"",
                   COUNT(""BookFiles"".""Id"") AS ""BookFileCount"",
                   SUM(CASE WHEN {_ebookExtensionCondition} THEN 1 ELSE 0 END) AS ""EbookFileCount"",
                   SUM(CASE WHEN {_audiobookExtensionCondition} THEN 1 ELSE 0 END) AS ""AudiobookFileCount"",
                   SUM(CASE WHEN {_ebookExtensionCondition} THEN COALESCE(""BookFiles"".""Size"", 0) ELSE 0 END) AS ""EbookSizeOnDisk"",
                   SUM(CASE WHEN {_audiobookExtensionCondition} THEN COALESCE(""BookFiles"".""Size"", 0) ELSE 0 END) AS ""AudiobookSizeOnDisk""
            FROM ""Editions""
            CROSS JOIN ""BookFiles""
            WHERE ""Editions"".""Id"" = ""BookFiles"".""EditionId""
              {authorFilter}
            GROUP BY ""Editions"".""BookId""
        ";
        }

        private static string BuildExtensionCondition(IEnumerable<string> extensions, string pathColumn)
        {
            var conditions = extensions
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .Select(extension =>
                    $"LOWER({pathColumn}) LIKE '%{extension.ToLowerInvariant().Replace("'", "''", StringComparison.Ordinal)}'")
                .ToList();

            return conditions.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", conditions)})";
        }
    }
}
