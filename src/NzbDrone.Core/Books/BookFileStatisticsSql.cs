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
    }
}
