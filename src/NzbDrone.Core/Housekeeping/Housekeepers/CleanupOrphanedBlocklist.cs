using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedBlocklist : IHousekeepingTask
    {
        private const int DeleteBatchSize = 900;

        private readonly IMainDatabase _database;

        public CleanupOrphanedBlocklist(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();

            var authorProviderIds = mapper.Query<string>(@"
                SELECT ""GoodreadsAuthorId"" FROM ""Authors"" WHERE ""GoodreadsAuthorId"" IS NOT NULL AND ""GoodreadsAuthorId"" != ''
                UNION
                SELECT ""HardcoverAuthorId"" FROM ""Authors"" WHERE ""HardcoverAuthorId"" IS NOT NULL AND ""HardcoverAuthorId"" != ''
                UNION
                SELECT ""OpenLibraryAuthorId"" FROM ""Authors"" WHERE ""OpenLibraryAuthorId"" IS NOT NULL AND ""OpenLibraryAuthorId"" != ''
                UNION
                SELECT ""AudnexusAuthorId"" FROM ""Authors"" WHERE ""AudnexusAuthorId"" IS NOT NULL AND ""AudnexusAuthorId"" != ''
                UNION
                SELECT ""GoogleBooksAuthorId"" FROM ""Authors"" WHERE ""GoogleBooksAuthorId"" IS NOT NULL AND ""GoogleBooksAuthorId"" != '';
            ").Where(id => id.IsNullOrWhiteSpace() == false)
              .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

            if (authorProviderIds.Count == 0)
            {
                return;
            }

            var blocklist = mapper.Query<Blocklist>(@"SELECT ""Id"", ""AuthorProviderIds"" FROM ""Blocklist""").ToList();
            if (blocklist.Empty())
            {
                return;
            }

            var orphanedIds = blocklist.Where(b =>
                    b.AuthorProviderIds == null ||
                    b.AuthorProviderIds.Count == 0 ||
                    !b.AuthorProviderIds.Any(id => authorProviderIds.Contains(id)))
                .Select(b => b.Id)
                .ToList();

            if (orphanedIds.Empty())
            {
                return;
            }

            DeleteByIds(mapper, orphanedIds);
        }

        private static void DeleteByIds(System.Data.IDbConnection connection, List<int> ids)
        {
            foreach (var batch in ids.Chunk(DeleteBatchSize))
            {
                var parameters = new DynamicParameters();
                var placeholders = new List<string>();

                var index = 0;
                foreach (var id in batch)
                {
                    var parameterName = $"id{index++}";
                    placeholders.Add("@" + parameterName);
                    parameters.Add(parameterName, id);
                }

                connection.Execute($@"DELETE FROM ""Blocklist"" WHERE ""Id"" IN ({placeholders.ConcatToString(", ")})", parameters);
            }
        }
    }
}
