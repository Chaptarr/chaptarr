namespace NzbDrone.Core.Books
{
    internal static class BookFileStatisticsSql
    {
        internal const string GroupedByBook = @"
            SELECT ""Editions"".""BookId"" AS ""BookId"",
                   SUM(""BookFiles"".""Size"") AS ""SizeOnDisk"",
                   COUNT(""BookFiles"".""Id"") AS ""BookFileCount""
            FROM ""BookFiles""
            CROSS JOIN ""Editions""
            WHERE ""Editions"".""Id"" = ""BookFiles"".""EditionId""
            GROUP BY ""Editions"".""BookId""
        ";

        // Same aggregate restricted to one author's books, so a single-author lookup does not
        // aggregate every file in the library. The id is an int, so inlining it is injection-safe.
        internal static string GroupedByBookForAuthor(int authorId) => $@"
            SELECT ""Editions"".""BookId"" AS ""BookId"",
                   SUM(""BookFiles"".""Size"") AS ""SizeOnDisk"",
                   COUNT(""BookFiles"".""Id"") AS ""BookFileCount""
            FROM ""BookFiles""
            CROSS JOIN ""Editions""
            WHERE ""Editions"".""Id"" = ""BookFiles"".""EditionId""
              AND ""Editions"".""BookId"" IN (SELECT ""Id"" FROM ""Books"" WHERE ""AuthorId"" = {authorId})
            GROUP BY ""Editions"".""BookId""
        ";
    }
}
