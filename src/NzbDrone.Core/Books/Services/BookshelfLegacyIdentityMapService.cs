using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class BookshelfLegacyIdentityMapEntry
    {
        public string Source { get; set; }
        public string EntityType { get; set; }
        public int SourceId { get; set; }
        public int TargetId { get; set; }
        public int? MediaType { get; set; }
    }

    public interface IBookshelfLegacyIdentityMapService
    {
        List<BookshelfLegacyIdentityMapEntry> GetMappings(string entityType, IEnumerable<int> sourceIds);
        List<BookshelfLegacyIdentityMapEntry> GetMappingsByTargetIds(string entityType, IEnumerable<int> targetIds);
        List<int> ResolveBookIds(IEnumerable<int> sourceIds, BookMediaType? mediaType = null);
    }

    public class BookshelfLegacyIdentityMapService : IBookshelfLegacyIdentityMapService
    {
        private const string SourceSystem = "BookshelfNG";
        private readonly IMainDatabase _database;

        public BookshelfLegacyIdentityMapService(IMainDatabase database)
        {
            _database = database;
        }

        public List<BookshelfLegacyIdentityMapEntry> GetMappings(string entityType, IEnumerable<int> sourceIds)
        {
            return GetMappingsByIds(entityType, sourceIds, "SourceId");
        }

        public List<BookshelfLegacyIdentityMapEntry> GetMappingsByTargetIds(string entityType, IEnumerable<int> targetIds)
        {
            return GetMappingsByIds(entityType, targetIds, "TargetId");
        }

        private List<BookshelfLegacyIdentityMapEntry> GetMappingsByIds(string entityType, IEnumerable<int> ids, string idColumn)
        {
            if (string.IsNullOrWhiteSpace(entityType) || (idColumn != "SourceId" && idColumn != "TargetId"))
            {
                return new List<BookshelfLegacyIdentityMapEntry>();
            }

            var distinctIds = ids?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (distinctIds.Count == 0)
            {
                return new List<BookshelfLegacyIdentityMapEntry>();
            }

            var mappings = new List<BookshelfLegacyIdentityMapEntry>();
            using var connection = _database.OpenConnection();
            for (var offset = 0; offset < distinctIds.Count; offset += 500)
            {
                var batch = distinctIds.Skip(offset).Take(500).ToList();
                var parameters = new DynamicParameters();
                parameters.Add("Source", SourceSystem);
                parameters.Add("EntityType", entityType.Trim());
                var parameterNames = new List<string>(batch.Count);
                for (var index = 0; index < batch.Count; index++)
                {
                    var parameterName = $"SourceId{index}";
                    parameterNames.Add("@" + parameterName);
                    parameters.Add(parameterName, batch[index]);
                }

                var sql = $@"
SELECT ""Source"", ""EntityType"", ""SourceId"", ""TargetId"", ""MediaType""
FROM ""BookshelfLegacyIdentityMap""
WHERE ""Source"" = @Source
  AND LOWER(""EntityType"") = LOWER(@EntityType)
  AND ""{idColumn}"" IN ({string.Join(", ", parameterNames)});"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- idColumn is allow-listed to fixed column names above.
                // Sorting in SQL can make SQLite scan the source-order index
                // instead of seeking TargetId. Sort only the selected rows here,
                // retaining each database's previous NULL ordering per batch.
                var nullsLast = connection is Npgsql.NpgsqlConnection;
                mappings.AddRange(connection.Query<BookshelfLegacyIdentityMapEntry>(sql, parameters)
                    .OrderBy(mapping => mapping.SourceId)
                    .ThenBy(mapping => nullsLast && !mapping.MediaType.HasValue)
                    .ThenBy(mapping => mapping.MediaType)
                    .ThenBy(mapping => mapping.TargetId));
            }

            return mappings;
        }

        public List<int> ResolveBookIds(IEnumerable<int> sourceIds, BookMediaType? mediaType = null)
        {
            var ids = sourceIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return new List<int>();
            }

            var mappingsBySourceId = GetMappings("Book", ids)
                .GroupBy(mapping => mapping.SourceId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var resolved = new List<int>();

            foreach (var sourceId in ids)
            {
                if (!mappingsBySourceId.TryGetValue(sourceId, out var mappings))
                {
                    resolved.Add(sourceId);
                    continue;
                }

                resolved.AddRange(mappings
                    .Where(mapping => !mediaType.HasValue || mapping.MediaType == (int)mediaType.Value)
                    .Select(mapping => mapping.TargetId));
            }

            return resolved.Distinct().ToList();
        }
    }
}
