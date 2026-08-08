using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace NzbDrone.Core.Datastore.Migration
{
    internal static class StringifiedProviderIdRepair
    {
        private const string PoisonPattern = "%System.Collections%";

        public static void Apply(IDbConnection connection, IDbTransaction transaction, bool isPostgres)
        {
            var affectedBookIds = GetAffectedBookIds(connection, transaction);
            var affectedAuthorIds = GetAffectedAuthorIds(connection, transaction, affectedBookIds, isPostgres);
            var idPredicate = IdPredicate(isPostgres, @"""Id""");
            var authorIdPredicate = IdPredicate(isPostgres, @"""AuthorId""");

            connection.Execute(@"
                UPDATE ""Books""
                SET ""ForeignEditionId"" = CASE WHEN ""ForeignEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""ForeignEditionId"" END,
                    ""GoodreadsBookId"" = CASE WHEN ""GoodreadsBookId"" LIKE @PoisonPattern THEN NULL ELSE ""GoodreadsBookId"" END,
                    ""GoodreadsWorkId"" = CASE WHEN ""GoodreadsWorkId"" LIKE @PoisonPattern THEN NULL ELSE ""GoodreadsWorkId"" END,
                    ""HardcoverBookId"" = CASE WHEN ""HardcoverBookId"" LIKE @PoisonPattern THEN NULL ELSE ""HardcoverBookId"" END,
                    ""OpenLibraryEditionId"" = CASE WHEN ""OpenLibraryEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""OpenLibraryEditionId"" END,
                    ""OpenLibraryWorkId"" = CASE WHEN ""OpenLibraryWorkId"" LIKE @PoisonPattern THEN NULL ELSE ""OpenLibraryWorkId"" END,
                    ""GoogleBooksId"" = CASE WHEN ""GoogleBooksId"" LIKE @PoisonPattern THEN NULL ELSE ""GoogleBooksId"" END,
                    ""ASIN"" = CASE WHEN ""ASIN"" LIKE @PoisonPattern THEN NULL ELSE ""ASIN"" END,
                    ""AudibleASIN"" = CASE WHEN ""AudibleASIN"" LIKE @PoisonPattern THEN NULL ELSE ""AudibleASIN"" END,
                    ""BaseBookId"" = CASE WHEN ""BaseBookId"" LIKE @PoisonPattern THEN NULL ELSE ""BaseBookId"" END
                WHERE ""ForeignEditionId"" LIKE @PoisonPattern
                   OR ""GoodreadsBookId"" LIKE @PoisonPattern
                   OR ""GoodreadsWorkId"" LIKE @PoisonPattern
                   OR ""HardcoverBookId"" LIKE @PoisonPattern
                   OR ""OpenLibraryEditionId"" LIKE @PoisonPattern
                   OR ""OpenLibraryWorkId"" LIKE @PoisonPattern
                   OR ""GoogleBooksId"" LIKE @PoisonPattern
                   OR ""ASIN"" LIKE @PoisonPattern
                   OR ""AudibleASIN"" LIKE @PoisonPattern
                   OR ""BaseBookId"" LIKE @PoisonPattern;",
                new { PoisonPattern },
                transaction);

            connection.Execute(@"
                UPDATE ""Editions""
                SET ""ForeignEditionId"" = CASE WHEN ""ForeignEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""ForeignEditionId"" END,
                    ""HardcoverEditionId"" = CASE WHEN ""HardcoverEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""HardcoverEditionId"" END,
                    ""OpenLibraryEditionId"" = CASE WHEN ""OpenLibraryEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""OpenLibraryEditionId"" END,
                    ""GoogleBooksEditionId"" = CASE WHEN ""GoogleBooksEditionId"" LIKE @PoisonPattern THEN NULL ELSE ""GoogleBooksEditionId"" END,
                    ""Asin"" = CASE WHEN ""Asin"" LIKE @PoisonPattern THEN NULL ELSE ""Asin"" END,
                    ""AudibleASIN"" = CASE WHEN ""AudibleASIN"" LIKE @PoisonPattern THEN NULL ELSE ""AudibleASIN"" END
                WHERE ""ForeignEditionId"" LIKE @PoisonPattern
                   OR ""HardcoverEditionId"" LIKE @PoisonPattern
                   OR ""OpenLibraryEditionId"" LIKE @PoisonPattern
                   OR ""GoogleBooksEditionId"" LIKE @PoisonPattern
                   OR ""Asin"" LIKE @PoisonPattern
                   OR ""AudibleASIN"" LIKE @PoisonPattern;",
                new { PoisonPattern },
                transaction);

            connection.Execute(
                @"DELETE FROM ""ProviderAliasIndex"" WHERE ""NormalizedProviderId"" LIKE @PoisonPattern;",
                new { PoisonPattern },
                transaction);

            if (affectedBookIds.Count > 0)
            {
                ExecuteChunked(
                    affectedBookIds,
                    ids => connection.Execute(
                        $@"UPDATE ""Books"" SET ""LastInfoSync"" = NULL WHERE {idPredicate};",
                        new { Ids = ids },
                        transaction));
            }

            if (affectedAuthorIds.Count > 0)
            {
                ExecuteChunked(
                    affectedAuthorIds,
                    ids => connection.Execute(
                        $@"UPDATE ""Authors"" SET ""LastInfoSync"" = NULL WHERE {idPredicate};",
                        new { Ids = ids },
                        transaction));

                ExecuteChunked(
                    affectedAuthorIds,
                    ids => connection.Execute(
                        $@"UPDATE ""AuthorSyncMetadata"" SET ""NextSyncNotBefore"" = NULL WHERE {authorIdPredicate};",
                        new { Ids = ids },
                        transaction));
            }
        }

        private static List<int> GetAffectedBookIds(IDbConnection connection, IDbTransaction transaction)
        {
            return connection.Query<int>(@"
                SELECT ""Id"" FROM ""Books""
                WHERE ""ForeignEditionId"" LIKE @PoisonPattern
                   OR ""GoodreadsBookId"" LIKE @PoisonPattern
                   OR ""GoodreadsWorkId"" LIKE @PoisonPattern
                   OR ""HardcoverBookId"" LIKE @PoisonPattern
                   OR ""OpenLibraryEditionId"" LIKE @PoisonPattern
                   OR ""OpenLibraryWorkId"" LIKE @PoisonPattern
                   OR ""GoogleBooksId"" LIKE @PoisonPattern
                   OR ""ASIN"" LIKE @PoisonPattern
                   OR ""AudibleASIN"" LIKE @PoisonPattern
                   OR ""BaseBookId"" LIKE @PoisonPattern
                UNION
                SELECT ""BookId"" FROM ""Editions""
                WHERE ""ForeignEditionId"" LIKE @PoisonPattern
                   OR ""HardcoverEditionId"" LIKE @PoisonPattern
                   OR ""OpenLibraryEditionId"" LIKE @PoisonPattern
                   OR ""GoogleBooksEditionId"" LIKE @PoisonPattern
                   OR ""Asin"" LIKE @PoisonPattern
                   OR ""AudibleASIN"" LIKE @PoisonPattern
                UNION
                SELECT ""EntityId"" FROM ""ProviderAliasIndex""
                WHERE ""EntityType"" = 'Book'
                  AND ""NormalizedProviderId"" LIKE @PoisonPattern
                UNION
                SELECT e.""BookId""
                FROM ""ProviderAliasIndex"" p
                JOIN ""Editions"" e ON e.""Id"" = p.""EntityId""
                WHERE p.""EntityType"" = 'Edition'
                  AND p.""NormalizedProviderId"" LIKE @PoisonPattern;",
                new { PoisonPattern },
                transaction)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        private static List<int> GetAffectedAuthorIds(IDbConnection connection, IDbTransaction transaction, List<int> affectedBookIds, bool isPostgres)
        {
            if (affectedBookIds == null || affectedBookIds.Count == 0)
            {
                return new List<int>();
            }

            var ids = new HashSet<int>();
            var idPredicate = IdPredicate(isPostgres, @"""Id""");

            ExecuteChunked(
                affectedBookIds,
                bookIds =>
                {
                    foreach (var authorId in connection.Query<int>(
                                 $@"SELECT DISTINCT ""AuthorId"" FROM ""Books"" WHERE {idPredicate} AND ""AuthorId"" > 0;",
                                 new { Ids = bookIds },
                                 transaction))
                    {
                        ids.Add(authorId);
                    }
                });

            return ids.ToList();
        }

        private static void ExecuteChunked(IReadOnlyList<int> ids, Action<int[]> action)
        {
            const int chunkSize = 500;
            for (var i = 0; i < ids.Count; i += chunkSize)
            {
                action(ids.Skip(i).Take(chunkSize).ToArray());
            }
        }

        private static string IdPredicate(bool isPostgres, string columnName)
        {
            return isPostgres ? columnName + " = ANY(@Ids)" : columnName + " IN @Ids";
        }
    }
}
