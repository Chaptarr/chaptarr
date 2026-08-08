using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface ISeriesBookLinkRepository : IBasicRepository<SeriesBookLink>
    {
        List<SeriesBookLink> GetLinksBySeries(int seriesId);
        List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId);
        List<SeriesBookLink> GetLinksByBook(List<int> bookIds);
        HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId);
    }

    public class SeriesBookLinkRepository : BasicRepository<SeriesBookLink>, ISeriesBookLinkRepository
    {
        public SeriesBookLinkRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<SeriesBookLink> GetLinksBySeries(int seriesId)
        {
            return Query(x => x.SeriesId == seriesId);
        }

        public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId)
        {
            // This method is deprecated - use GetLinksBySeries instead
            // Series links are no longer filtered by author since ForeignAuthorId has been removed
            return GetLinksBySeries(seriesId);
        }

        public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId)
        {
            if (string.IsNullOrWhiteSpace(goodreadsSeriesId))
            {
                return new HashSet<int>();
            }

            var gr = goodreadsSeriesId.Trim();

            var sql = @"SELECT DISTINCT sbl.""BookId""
                        FROM ""SeriesBookLink"" sbl
                        INNER JOIN ""Series"" s ON s.""Id"" = sbl.""SeriesId""
                        WHERE (s.""PreferredNarratorId"" IS NOT NULL OR (s.""Narrator"" IS NOT NULL AND TRIM(s.""Narrator"") <> ''))
                          AND s.""MediaType"" = @MediaType
                          AND s.""GoodreadsSeriesId"" = @GoodreadsSeriesId";

            var ids = _database.Query<int>(sql, new
            {
                MediaType = (int)mediaType,
                GoodreadsSeriesId = gr
            });

            return ids.ToHashSet();
        }

	        public List<SeriesBookLink> GetLinksByBook(List<int> bookIds)
	        {
	            if (bookIds == null || bookIds.Count == 0)
	            {
	                return new List<SeriesBookLink>();
	            }

	            // Use simple queries instead of Dapper multi-mapping: list expansion for IN(...) is
	            // reliable in simple Query<T> but can break in multi-map on some providers.
	            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
	            var isSqlite = _database.DatabaseType == DatabaseType.SQLite;
	            var bookIdsArray = bookIds.Distinct().ToArray();
	            var linksSql = isPostgres
	                ? @"SELECT sbl.* FROM ""SeriesBookLink"" sbl WHERE sbl.""BookId"" = ANY(@BookIds)"
	                : @"SELECT sbl.* FROM ""SeriesBookLink"" sbl WHERE sbl.""BookId"" IN @BookIds";

	            // Use array so Dapper doesn't treat List<int> as JSON on some providers.
	            // SQLite has a default ~999 bind-variable limit; Dapper expands IN lists into many parameters, so chunk.
	            var links = new List<SeriesBookLink>();
	            if (isSqlite && bookIdsArray.Length > SqliteVariableLimit.MaxParameters)
	            {
	                foreach (var batch in bookIdsArray.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    links.AddRange(_database.Query<SeriesBookLink>(linksSql, new { BookIds = batch.ToArray() }));
	                }

	                // Deduplicate in case of repeated ids and to protect against future query changes.
	                links = links.DistinctBy(l => l.Id).ToList();
	            }
	            else
	            {
	                links = _database.Query<SeriesBookLink>(linksSql, new { BookIds = bookIdsArray }).ToList();
	            }

	            if (links.Count == 0)
	            {
	                return links;
	            }

            // Custom query to avoid loading the Books JSON column which causes serialization errors
            var seriesWhereClause = isPostgres ? @"= ANY(@SeriesIds)" : @"IN @SeriesIds";
            var seriesSql = $@"SELECT
                s.""Id"" AS ""Id"", s.""Title"" AS ""Title"", s.""TitleSlug"" AS ""TitleSlug"", s.""Description"" AS ""Description"", s.""Numbered"" AS ""Numbered"", s.""WorkCount"" AS ""WorkCount"", s.""PrimaryWorkCount"" AS ""PrimaryWorkCount"",
                s.""GoodreadsSeriesId"" AS ""GoodreadsSeriesId"", s.""HardcoverSeriesId"" AS ""HardcoverSeriesId"", s.""OpenLibrarySeriesId"" AS ""OpenLibrarySeriesId"", s.""AmazonSeriesAsin"" AS ""AmazonSeriesAsin"", s.""SeriesType"" AS ""SeriesType"",
                s.""ParentSeriesId"" AS ""ParentSeriesId"", s.""TotalBooks"" AS ""TotalBooks"", s.""PrimaryBooks"" AS ""PrimaryBooks"", s.""Narrator"" AS ""Narrator"", s.""BaseSeriesId"" AS ""BaseSeriesId"",
                s.""MediaType"" AS ""MediaType"", s.""InstanceNumber"" AS ""InstanceNumber"", s.""PreferredNarratorId"" AS ""PreferredNarratorId"",
                s.""ProviderUrls"" AS ""ProviderUrls"", s.""LastUpdated"" AS ""LastUpdated"", s.""Links"" AS ""Links""
	                FROM ""Series"" s
	                WHERE s.""Id"" {seriesWhereClause}";

	            var seriesIds = links.Select(l => l.SeriesId).Distinct().ToArray();
	            var seriesById = new Dictionary<int, Series>();
	            if (isSqlite && seriesIds.Length > SqliteVariableLimit.MaxParameters)
	            {
	                foreach (var batch in seriesIds.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    var batchSeries = _database.Query<Series>(seriesSql, new { SeriesIds = batch.ToArray() });
	                    foreach (var series in batchSeries)
	                    {
	                        seriesById.TryAdd(series.Id, series);
	                    }
	                }
	            }
	            else
	            {
	                seriesById = _database.Query<Series>(seriesSql, new { SeriesIds = seriesIds }).ToDictionary(s => s.Id);
	            }

	            foreach (var link in links)
	            {
	                if (seriesById.TryGetValue(link.SeriesId, out var series))
	                {
                    link.Series = new LazyLoaded<Series>(series);
                }
            }

            return links;
        }
    }
}
