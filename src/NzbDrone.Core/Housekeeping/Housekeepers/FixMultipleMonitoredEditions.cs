using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Converters;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class FixMultipleMonitoredEditions : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public FixMultipleMonitoredEditions(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            Repair(connection, transaction, _database.DatabaseType, LogManager.GetCurrentClassLogger(), duplicatesOnly: false);
            transaction.Commit();
        }

        private sealed class RepairRow
        {
            public int Id { get; set; }
            public int BookId { get; set; }
            public bool Monitored { get; set; }
            public bool ManualAdd { get; set; }
            public int ReadingFormatId { get; set; }
            public string RatingsJson { get; set; }
            public int FileCount { get; set; }
            public BookMediaType MediaType { get; set; }
            public bool AnyEditionOk { get; set; }
        }

        internal static int Repair(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, Logger logger, bool duplicatesOnly, int? commandTimeout = null)
        {
            var afterId = 0;
            var repaired = 0;
            var ratings = new EmbeddedDocumentConverter<Ratings>();
            while (true)
            {
                var bookIds = connection.Query<int>($@"SELECT e.""BookId"" FROM ""Editions"" e
                    JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                    WHERE e.""BookId"" > @afterId GROUP BY e.""BookId""
                    HAVING SUM(CASE WHEN e.""Monitored"" THEN 1 ELSE 0 END) {(duplicatesOnly ? ">" : "<>")} 1
                    ORDER BY e.""BookId"" LIMIT {SqliteVariableLimit.MaxParameters}", new { afterId }, transaction, commandTimeout: commandTimeout).ToArray();
                if (bookIds.Length == 0) return repaired;

                EditionRepository.LockSelectionBooks(bookIds, connection, transaction, databaseType, commandTimeout);
                var bookPredicate = databaseType == DatabaseType.PostgreSQL ? "= ANY(@bookIds)" : "IN @bookIds";
                var rows = connection.Query<RepairRow>($@"SELECT e.""Id"", e.""BookId"", e.""Monitored"", e.""ManualAdd"",
                    e.""ReadingFormatId"", e.""Ratings"" AS ""RatingsJson"", b.""MediaType"", b.""AnyEditionOk"",
                    (SELECT COUNT(*) FROM ""BookFiles"" f WHERE f.""EditionId"" = e.""Id"") AS ""FileCount""
                    FROM ""Editions"" e JOIN ""Books"" b ON b.""Id"" = e.""BookId"" WHERE e.""BookId"" {bookPredicate}",
                    new { bookIds }, transaction, commandTimeout: commandTimeout).ToList();
                var changed = new List<Edition>();
                foreach (var group in rows.GroupBy(row => row.BookId))
                {
                    var monitoredCount = group.Count(row => row.Monitored);
                    if (monitoredCount == 1 || (duplicatesOnly && monitoredCount == 0)) continue;
                    var editions = group.Select(row => new Edition
                    {
                        Id = row.Id, BookId = row.BookId, Monitored = row.Monitored, ManualAdd = row.ManualAdd,
                        ReadingFormatId = row.ReadingFormatId,
                        Ratings = string.IsNullOrWhiteSpace(row.RatingsJson) ? null : ratings.Parse(row.RatingsJson)
                    }).ToList();
                    var selected = EditionSelector.SelectRepairEdition(editions,
                        group.ToDictionary(row => row.Id, row => row.FileCount), group.First().MediaType, group.First().AnyEditionOk);
                    foreach (var edition in editions)
                    {
                        edition.Monitored = edition.Id == selected.Id;
                    }
                    changed.AddRange(editions);
                    repaired++;
                    logger.Info("Edition monitoring repair: BookId={0}, selected EditionId={1}", group.Key, selected.Id);
                }

                EditionRepository.PersistSelection(changed, connection, transaction, databaseType, commandTimeout);
                afterId = bookIds[^1];
            }
        }
    }
}
