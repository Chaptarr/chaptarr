namespace NzbDrone.Core.Books
{
    internal static class BookFileStatisticsSql
    {
        private const string _select = @"
            SELECT ""Editions"".""BookId"" AS ""BookId"",
                   SUM(""BookFiles"".""Size"") AS ""SizeOnDisk"",
                   COUNT(""BookFiles"".""Id"") AS ""BookFileCount""";

        private const string _groupBy = @"
            GROUP BY ""Editions"".""BookId""
        ";

        internal const string GroupedByBook = _select + @"
            FROM ""BookFiles""
            CROSS JOIN ""Editions""
            WHERE ""Editions"".""Id"" = ""BookFiles"".""EditionId""" + _groupBy;

        internal static string GroupedByBookForAuthors(string authorIdPredicate) => _select + $@"
            FROM ""Books""
            INNER JOIN ""Editions"" ON ""Editions"".""BookId"" = ""Books"".""Id""
            INNER JOIN ""BookFiles"" ON ""BookFiles"".""EditionId"" = ""Editions"".""Id""
            WHERE ""Books"".""AuthorId"" {authorIdPredicate}" + _groupBy;
    }
}
