using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryRepository : IBasicRepository<DownloadHistory>
    {
        List<DownloadHistory> FindByDownloadId(string downloadId);
        List<DownloadHistory> GetPendingLabelChanges(int downloadClientId);
        List<DownloadHistory> GetLatestDownloadHistoryItems(IEnumerable<string> downloadIds);
        List<DownloadHistory> GetByAuthorId(int authorId);
        List<DownloadHistory> FindByIds(IEnumerable<int> ids);
        PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec);
        void DeleteIgnoredByDownloadIds(IEnumerable<string> downloadIds);
        void DeleteByAuthorId(int authorId);
    }

    public class DownloadHistoryRepository : BasicRepository<DownloadHistory>, IDownloadHistoryRepository
    {
        public DownloadHistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<DownloadHistory> GetPendingLabelChanges(int downloadClientId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<DownloadHistory>(@"SELECT * FROM ""DownloadHistory""
                WHERE ""DownloadClientId"" = @downloadClientId AND ""EventType"" = @grabbed
                AND ""Data"" LIKE @marker", new
            {
                downloadClientId,
                grabbed = (int)DownloadHistoryEventType.DownloadGrabbed,
                marker = "%\"" + DownloadHistory.PendingLabelChangeKey + "\"%"
            }).ToList();
        }

        public List<DownloadHistory> FindByDownloadId(string downloadId)
        {
            return Query(h => h.DownloadId == downloadId)
                .OrderByDescending(h => h.Date)
                .ThenByDescending(h => h.Id)
                .ToList();
        }

        public List<DownloadHistory> GetLatestDownloadHistoryItems(IEnumerable<string> downloadIds)
        {
            var ids = downloadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0)
            {
                return new List<DownloadHistory>();
            }

            var significantEvents = SignificantEvents();
            var chunkSize = _database.DatabaseType == DatabaseType.PostgreSQL ? ids.Length : SqliteVariableLimit.MaxParameters - significantEvents.Length;
            var sql = $@"{BuildCurrentSignificantEventsCte(_database.DatabaseType, scoped: true)}
                SELECT * FROM significant WHERE ""RowNumber"" = 1";
            using var connection = _database.OpenConnection();
            return ids.Chunk(chunkSize).SelectMany(chunk => connection.Query<DownloadHistory>(sql,
                new { downloadIds = chunk, significantEvents })).ToList();
        }

        public List<DownloadHistory> GetByAuthorId(int authorId)
        {
            return Query(history => history.AuthorId == authorId);
        }

        public List<DownloadHistory> FindByIds(IEnumerable<int> ids)
        {
            var idList = ids?
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            if (!idList.Any())
            {
                return new List<DownloadHistory>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && idList.Count > SqliteVariableLimit.MaxParameters)
            {
                var result = new List<DownloadHistory>();
                foreach (var chunk in idList.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var chunkIds = chunk.ToArray();
                    result.AddRange(Query(x => Enumerable.Contains(chunkIds, x.Id)));
                }

                return result.DistinctBy(h => h.Id).ToList();
            }

            return Query(x => idList.Contains(x.Id));
        }

        public PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec)
        {
            var sortDirection = pagingSpec.SortDirection == SortDirection.Ascending ? "ASC" : "DESC";
            var sortColumn = GetIgnoredSortColumn(pagingSpec.SortKey);
            var page = pagingSpec.Page <= 0 ? 1 : pagingSpec.Page;
            var pageSize = pagingSpec.PageSize <= 0 ? 20 : pagingSpec.PageSize;
            var offset = (page - 1) * pageSize;
            var parameters = new
            {
                significantEvents = SignificantEvents(),
                ignoredEvent = (int)DownloadHistoryEventType.DownloadIgnored,
                offset,
                pageSize
            };

            var cte = BuildCurrentSignificantEventsCte(_database.DatabaseType);

            using (var conn = _database.OpenConnection())
            {
                pagingSpec.TotalRecords = conn.ExecuteScalar<int>($@"
                    {cte}
                    SELECT COUNT(*)
                    FROM significant WHERE ""RowNumber"" = 1 AND ""EventType"" = @ignoredEvent;", parameters);

                pagingSpec.Records = conn.Query<DownloadHistory>($@"
                    {cte}
                    SELECT
                        ""Id"",
                        ""EventType"",
                        ""AuthorId"",
                        ""BookId"",
                        ""DownloadId"",
                        ""SourceTitle"",
                        ""Date"",
                        ""Protocol"",
                        ""IndexerId"",
                        ""DownloadClientId"",
                        ""Release"",
                        ""Data""
                    FROM significant WHERE ""RowNumber"" = 1 AND ""EventType"" = @ignoredEvent
                    ORDER BY {sortColumn} {sortDirection}, ""Id"" DESC
                    LIMIT @pageSize OFFSET @offset;", parameters).ToList();
            }

            return pagingSpec;
        }

        public void DeleteIgnoredByDownloadIds(IEnumerable<string> downloadIds)
        {
            var ids = downloadIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(DownloadIdentity.Key)
                .Distinct()
                .ToList() ?? new List<string>();

            if (!ids.Any())
            {
                return;
            }

            using (var conn = _database.OpenConnection())
            {
                var sql = BuildDeleteIgnoredByDownloadIdsSql(_database.DatabaseType);

                foreach (var chunk in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    conn.Execute(sql,
                        new
                        {
                            ignoredEvent = (int)DownloadHistoryEventType.DownloadIgnored,
                            downloadIds = chunk.ToArray()
                        });
                }
            }
        }

        public void DeleteByAuthorId(int authorId)
        {
            var preserveValue = _database.DatabaseType == DatabaseType.PostgreSQL
                ? @"""Data""::jsonb ->> @keepSeedingKey"
                : @"json_extract(""Data"", @keepSeedingPath)";
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var parameters = new
            {
                authorId,
                grabbed = (int)DownloadHistoryEventType.DownloadGrabbed,
                keepSeedingKey = DownloadHistory.KeepSeedingKey,
                keepSeedingPath = "$." + DownloadHistory.KeepSeedingKey,
                enabled = bool.TrueString
            };
            connection.Execute($@"UPDATE ""DownloadHistory"" SET ""AuthorId"" = 0, ""BookId"" = 0
                WHERE ""AuthorId"" = @authorId AND ""EventType"" = @grabbed AND {preserveValue} = @enabled", parameters, transaction);
            connection.Execute(@"DELETE FROM ""DownloadHistory"" WHERE ""AuthorId"" = @authorId", parameters, transaction);
            transaction.Commit();
        }

        internal static int[] SignificantEvents()
        {
            return new[]
            {
                (int)DownloadHistoryEventType.DownloadIgnored,
                (int)DownloadHistoryEventType.DownloadGrabbed,
                (int)DownloadHistoryEventType.DownloadImported,
                (int)DownloadHistoryEventType.DownloadFailed,
                (int)DownloadHistoryEventType.DownloadImportIncomplete
            };
        }

        internal const string LatestEventOrder = "\"Date\" DESC, \"Id\" DESC";

        internal static string BuildCurrentSignificantEventsCte(DatabaseType databaseType, bool scoped = false, bool perClient = false)
        {
            var eventPredicate = databaseType == DatabaseType.PostgreSQL ? @"= ANY(@significantEvents)" : @"IN @significantEvents";
            var idPredicate = databaseType == DatabaseType.PostgreSQL ? @"= ANY(@downloadIds)" : @"IN @downloadIds";

            return $@"
                WITH significant AS (
                    SELECT
                        ""DownloadHistory"".*,
                        ROW_NUMBER() OVER (PARTITION BY ""DownloadId""{(perClient ? ", \"DownloadClientId\"" : string.Empty)} ORDER BY {LatestEventOrder}) AS ""RowNumber""
                    FROM ""DownloadHistory""
                    WHERE ""DownloadId"" IS NOT NULL
                      AND ""DownloadId"" <> ''
                      AND ""EventType"" {eventPredicate}
                      {(scoped ? $"AND \"DownloadId\" {idPredicate}" : string.Empty)}
                )";
        }

        internal static string BuildDeleteIgnoredByDownloadIdsSql(DatabaseType databaseType)
        {
            var downloadIdPredicate = databaseType == DatabaseType.PostgreSQL ? @"= ANY(@downloadIds)" : @"IN @downloadIds";

            return $@"
                        DELETE FROM ""DownloadHistory""
                        WHERE ""EventType"" = @ignoredEvent
                          AND ""DownloadId"" {downloadIdPredicate};";
        }

        private static string GetIgnoredSortColumn(string sortKey)
        {
            switch (sortKey?.Trim().ToLowerInvariant())
            {
                case "sourcetitle":
                    return @"""SourceTitle""";
                case "downloadid":
                    return @"""DownloadId""";
                case "downloadclientid":
                    return @"""DownloadClientId""";
                case "protocol":
                    return @"""Protocol""";
                case "date":
                default:
                    return @"""Date""";
            }
        }
    }
}
