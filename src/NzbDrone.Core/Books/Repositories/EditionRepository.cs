using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using Dapper;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public interface IEditionRepository : IBasicRepository<Edition>
    {
        void RefreshProviderAliases(IList<Edition> editions);
        List<Edition> GetAllMonitoredEditions();
        Edition FindByForeignEditionId(string foreignEditionId);
        List<Edition> FindAllByForeignEditionId(string foreignEditionId);
        List<Edition> FindAllByForeignEditionId(IEnumerable<string> ids);
        Edition FindByHardcoverEditionId(string hardcoverEditionId);
        List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId);
        List<Edition> FindAllByHardcoverEditionId(IEnumerable<string> ids);
        Edition FindByGoodreadsEditionId(long goodreadsEditionId);
        List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId);
        Edition FindByOpenLibraryEditionId(string openLibraryEditionId);
        List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId);
        Edition FindByGoogleBooksEditionId(string googleBooksEditionId);
        List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId);
        List<Edition> FindByBook(IEnumerable<int> ids);
        List<Edition> FindRowsByBook(IEnumerable<int> ids) => FindByBook(ids);
        List<Edition> FindByAuthor(int id, bool onlyMonitored = false);
        List<Edition> GetEditionsForRefresh(int bookId);
        List<Edition> SetMonitored(Edition edition, bool isManualSelection = false);
        HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs);
        Dictionary<string, List<Edition>> FindAllByProviderIds(string provider, IEnumerable<string> ids) => throw new NotImplementedException();
        List<Edition> FindAllByAsin(string asin);
        List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType);
        Edition FindByIsbn(string isbn);
        List<Edition> FindAllByIsbn(string isbn);
        int CountMissingMatchingTitles();
        List<Edition> GetMissingMatchingTitles(int afterId, int limit);
    }

    public class EditionRepository : BasicRepository<Edition>, IEditionRepository
    {
        private readonly Logger _logger;
        private readonly ProviderAliasRepository _aliasRepository;

        public EditionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
            _logger = LogManager.GetCurrentClassLogger();
            _aliasRepository = new ProviderAliasRepository(database, eventAggregator);
        }

        public override Edition Insert(Edition edition)
        {
            InsertMany(new List<Edition> { edition });
            ModelCreated(edition);
            return edition;
        }

        public override void InsertMany(IList<Edition> editions)
        {
            if (editions.Count == 0) return;
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            InsertMany(editions, connection, transaction);
            transaction.Commit();
        }

        public override void InsertMany(IList<Edition> editions, IDbConnection connection, IDbTransaction transaction)
        {
            base.InsertMany(editions, connection, transaction);
            ReplaceEditionAliases(editions, connection, transaction, replaceExisting: false);
        }

        public override Edition Update(Edition edition)
        {
            UpdateMany(new List<Edition> { edition });
            ModelUpdated(edition);
            return edition;
        }

        public override void UpdateMany(IList<Edition> editions)
        {
            if (editions.Count == 0) return;
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            base.UpdateMany(editions, connection, transaction);
            ReplaceEditionAliases(editions, connection, transaction);
            transaction.Commit();
        }

        public override void DeleteMany(IEnumerable<int> ids)
        {
            var editionIds = ids?.Distinct().ToList();
            if (editionIds?.Count is not > 0) return;
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            base.DeleteMany(editionIds, connection, transaction);
            DeleteEditionAliases(editionIds, connection, transaction);
            transaction.Commit();
        }

        private void DeleteEditionAliases(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction)
        {
            foreach (var chunk in ids.Distinct().Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : ushort.MaxValue))
            {
                var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                connection.Execute($"DELETE FROM \"ProviderAliasIndex\" WHERE \"EntityType\" = 'Edition' AND \"Scope\" = 'edition' AND \"EntityId\" {inClause}", new { ids = chunk.ToArray() }, transaction);
            }
        }

        public void RefreshProviderAliases(IList<Edition> editions)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            ReplaceEditionAliases(editions, connection, transaction);
            transaction.Commit();
        }

        private void ReplaceEditionAliases(IList<Edition> editions, IDbConnection connection, IDbTransaction transaction, bool replaceExisting = true)
        {
            if (editions.Count == 0) return;
            // Reloaded rows have no fresh provider set. Preserve their attested aliases;
            // explicit upstream sets replace withdrawn aliases during metadata refresh.
            var existing = (replaceExisting ? ReadEditionAliases(editions.Where(edition => edition.RemoteProviderIds == null).Select(edition => edition.Id), connection, transaction)
                : new List<ProviderAlias>()).ToLookup(alias => alias.EntityId);
            var now = DateTime.UtcNow;
            var aliases = editions.SelectMany(edition => ProviderAliasService.NormalizeAliases(
                BookEditionIdentity.GetEditionProviderIds(edition).Select(BookEditionIdentity.NormalizeRequestedEditionProviderId)
                    .Concat(existing[edition.Id].Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}")))
                .Select(alias => new ProviderAlias
                {
                    EntityType = "Edition", EntityId = edition.Id, Scope = "edition", Provider = alias.Provider,
                    NormalizedProviderId = alias.NormalizedProviderId, CreatedAt = now, UpdatedAt = now
                })).ToList();
            if (replaceExisting) DeleteEditionAliases(editions.Select(edition => edition.Id), connection, transaction);
            _aliasRepository.InsertMany(aliases, connection, transaction);
        }

        private List<ProviderAlias> ReadEditionAliases(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction = null)
        {
            var aliases = new List<ProviderAlias>();
            foreach (var chunk in ids.Distinct().Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : ushort.MaxValue))
            {
                var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                aliases.AddRange(connection.Query<ProviderAlias>($"SELECT * FROM \"ProviderAliasIndex\" WHERE \"EntityType\" = 'Edition' AND \"Scope\" = 'edition' AND \"EntityId\" {inClause}", new { ids = chunk.ToArray() }, transaction));
            }
            return aliases;
        }

        private List<Edition> HydrateEditionAliases(List<Edition> editions)
        {
            if (editions.Count == 0) return editions;
            using var connection = _database.OpenConnection();
            var aliases = ReadEditionAliases(editions.Select(edition => edition.Id), connection).ToLookup(alias => alias.EntityId);
            foreach (var edition in editions)
            {
                edition.RemoteProviderIds = aliases[edition.Id].Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            return editions;
        }

        public List<Edition> GetAllMonitoredEditions()
        {
            // WhereBuilderSqlite requires a concrete value; avoid boolean-expression simplification.
            var monitored = true;
            return Query(x => x.Monitored == monitored);
        }

        public int CountMissingMatchingTitles()
        {
            var sql = $"SELECT COUNT(*) FROM \"{_table}\" WHERE (\"MatchingTitle\" IS NULL OR \"MatchingTitle\" = '') AND (\"Title\" IS NOT NULL AND \"Title\" <> '')";
            using (var conn = _database.OpenConnection())
            {
                return conn.ExecuteScalar<int>(sql);
            }
        }

        public List<Edition> GetMissingMatchingTitles(int afterId, int limit)
        {
            var sql = $"SELECT * FROM \"{_table}\" WHERE \"Id\" > @afterId AND (\"MatchingTitle\" IS NULL OR \"MatchingTitle\" = '') AND (\"Title\" IS NOT NULL AND \"Title\" <> '') ORDER BY \"Id\" LIMIT @limit";
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<Edition>(sql, new { afterId, limit }).ToList();
            }
        }

        public Edition FindByForeignEditionId(string foreignEditionId)
        {
            return ChooseOldest(FindAllByForeignEditionId(foreignEditionId), nameof(Edition.ForeignEditionId), foreignEditionId);
        }

        public List<Edition> FindAllByForeignEditionId(string foreignEditionId)
        {
            return FindAllByForeignEditionId(new[] { foreignEditionId });
        }

        public List<Edition> FindAllByForeignEditionId(IEnumerable<string> ids)
        {
            var values = ids.Distinct().ToArray();
            return values.Length == 0 ? new List<Edition>() : Query(x => Enumerable.Contains(values, x.ForeignEditionId))
                .OrderBy(x => x.Id).ToList();
        }

        public Edition FindByHardcoverEditionId(string hardcoverEditionId)
        {
            return ChooseOldest(FindAllByHardcoverEditionId(hardcoverEditionId), nameof(Edition.HardcoverEditionId), hardcoverEditionId);
        }

        public List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId)
        {
            return FindAllByHardcoverEditionId(new[] { hardcoverEditionId });
        }

        public List<Edition> FindAllByHardcoverEditionId(IEnumerable<string> ids)
        {
            var values = ids.Distinct().ToArray();
            return values.Length == 0 ? new List<Edition>() : Query(x => Enumerable.Contains(values, x.HardcoverEditionId))
                .OrderBy(x => x.Id).ToList();
        }

        public Edition FindByGoodreadsEditionId(long goodreadsEditionId)
        {
            return ChooseOldest(FindAllByGoodreadsEditionId(goodreadsEditionId), nameof(Edition.GoodreadsEditionId), goodreadsEditionId.ToString());
        }

        public List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId)
        {
            return Query(x => x.GoodreadsEditionId == goodreadsEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByOpenLibraryEditionId(string openLibraryEditionId)
        {
            return ChooseOldest(FindAllByOpenLibraryEditionId(openLibraryEditionId), nameof(Edition.OpenLibraryEditionId), openLibraryEditionId);
        }

        public List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId)
        {
            return Query(x => x.OpenLibraryEditionId == openLibraryEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByGoogleBooksEditionId(string googleBooksEditionId)
        {
            return ChooseOldest(FindAllByGoogleBooksEditionId(googleBooksEditionId), nameof(Edition.GoogleBooksEditionId), googleBooksEditionId);
        }

        public List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId)
        {
            return Query(x => x.GoogleBooksEditionId == googleBooksEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        private Edition ChooseOldest(List<Edition> editions, string field, string value, bool expectedPlural = false)
        {
            var matches = (editions ?? new List<Edition>())
                .Where(e => e != null)
                .OrderBy(e => e.Id)
                .ToList();

            var chosen = matches.FirstOrDefault();
            if (matches.Count > 1)
            {
                var message = "Found {0} editions with {1} {2}; legacy singular lookup using oldest (Id={3})";
                if (expectedPlural)
                {
                    _logger.Debug(message, matches.Count, field, value, chosen?.Id);
                }
                else
                {
                    _logger.Warn(message, matches.Count, field, value, chosen?.Id);
                }
            }

            return chosen;
        }

        public List<Edition> GetEditionsForRefresh(int bookId)
        {
            // IMPORTANT: In Chaptarr's multi-copy architecture, ForeignEditionId is not globally unique.
            // Each physical book copy has its own set of Edition rows (and thus duplicated ForeignEditionIds).
            // Refresh must only load editions for the specific BookId being refreshed; otherwise editions (and attached
            // files via EditionId) can get re-parented across copies, collapsing multiple books into one.
            return HydrateEditionAliases(Query(r => r.BookId == bookId));
        }

        public List<Edition> FindByBook(IEnumerable<int> ids) => FindByBookInternal(ids, hydrateBook: true);

        public List<Edition> FindRowsByBook(IEnumerable<int> ids) => FindByBookInternal(ids, hydrateBook: false);

        private List<Edition> FindByBookInternal(IEnumerable<int> ids, bool hydrateBook)
        {
            if (ids == null)
            {
                return new List<Edition>();
            }

            var bookIds = ids.Distinct().ToArray();
            if (bookIds.Length == 0)
            {
                return new List<Edition>();
            }

            List<Edition> QueryForBookIds(int[] chunkBookIds)
            {
                if (!hydrateBook)
                {
                    return Query(edition => Enumerable.Contains(chunkBookIds, edition.BookId));
                }

                var builder = new SqlBuilder(_database.DatabaseType)
                    .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                    .LeftJoin<Book, Author>((b, au) => b.AuthorId == au.Id)
	                    .Where<Edition>(r => Enumerable.Contains(chunkBookIds, r.BookId));

                return _database.QueryJoined<Edition, Book, Author>(builder, (edition, book, author) =>
                        {
                            if (book != null)
                            {
                                book.Author = author;
                                edition.Book = book;
                            }

                            return edition;
                        }).ToList();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && bookIds.Length > SqliteVariableLimit.MaxParameters)
            {
                var editions = new List<Edition>();
                foreach (var batch in bookIds.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    editions.AddRange(QueryForBookIds(batch.ToArray()));
                }

                return HydrateEditionAliases(editions.DistinctBy(e => e.Id).ToList());
            }

            return HydrateEditionAliases(QueryForBookIds(bookIds));
        }

        public List<Edition> FindByAuthor(int id, bool onlyMonitored = false)
        {
            var builder = Builder().Join<Edition, Book>((e, b) => e.BookId == b.Id)
                .Join<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Author>(a => a.Id == id);
            if (onlyMonitored)
            {
                // Keep the ownership join and author predicate while giving the monitored lookup the indexed book-id set first.
                builder.Where("\"Editions\".\"BookId\" IN (SELECT \"Id\" FROM \"Books\" WHERE \"AuthorId\" = @authorId)", new { authorId = id });
                var monitored = true;
                builder.Where<Edition>(e => e.Monitored == monitored);
            }

            return Query(builder);
        }

        public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
        {
            var allEditions = FindByBook(new[] { edition.BookId });
            allEditions.ForEach(r => {
                if (r.Id == edition.Id)
                {
                    r.Monitored = true;
                    // CRITICAL: Only mark as manually selected if this was actually a manual selection
                    // Don't set ManualAdd=true for automatic imports from root folders
                    if (isManualSelection)
                    {
                        r.ManualAdd = true;
                        _logger.Debug("Setting ManualAdd=true for edition {0} due to manual selection", r.Id);
                    }
                }
                else
                {
                    r.Monitored = false;
                    // ManualAdd is the persisted user-selected edition. It must not
                    // float independently from the single monitored edition.
                    r.ManualAdd = false;
                }
            });

            // With multi-edition architecture, we might have books with different editions in separate directories
            // Ensure at least one edition is monitored, but allow for cases where no editions exist yet
            var monitoredCount = allEditions.Count(x => x.Monitored);
            if (allEditions.Any())
            {
                Ensure.That(monitoredCount <= 1).IsTrue();
                if (monitoredCount == 0)
                {
                    // If no edition is monitored, monitor the first available edition
                    var firstEdition = allEditions.FirstOrDefault();
                    if (firstEdition != null)
                    {
                        firstEdition.Monitored = true;
                        // Note: Don't set ManualAdd here since this is automatic selection
                    }
                }
            }

            UpdateMany(allEditions);
            return allEditions;
        }

        public HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;

            var baseSlugs = baseTitleSlugs?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(comparer)
                .ToArray() ?? Array.Empty<string>();

            var existing = new HashSet<string>(comparer);
            if (baseSlugs.Length == 0)
            {
                return existing;
            }

            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var chunkSize = isPostgres ? 5000 : SqliteVariableLimit.MaxParameters / 2;

            foreach (var chunk in baseSlugs.Chunk(chunkSize))
            {
                var sql = new StringBuilder();
                var parameters = new DynamicParameters();

                parameters.Add("slugs", isPostgres ? chunk.ToArray() : chunk);

                var inClause = isPostgres ? "= ANY(@slugs)" : "IN @slugs";
                sql.Append($"SELECT DISTINCT \"TitleSlug\" FROM \"{_table}\" WHERE \"TitleSlug\" IS NOT NULL AND (\"TitleSlug\" {inClause}");

                var i = 0;
                foreach (var slug in chunk)
                {
                    var paramName = $"p{i}";
                    parameters.Add(paramName, EscapeLikePattern(slug) + "\\_%");
                    sql.Append($" OR \"TitleSlug\" LIKE @{paramName} ESCAPE '\\'");
                    i++;
                }

                sql.Append(")");

                foreach (var slug in _database.Query<string>(sql.ToString(), parameters))
                {
                    if (!string.IsNullOrWhiteSpace(slug))
                    {
                        existing.Add(slug.Trim());
                    }
                }
            }

            return existing;
        }

        private static string EscapeLikePattern(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        public Dictionary<string, List<Edition>> FindAllByProviderIds(string provider, IEnumerable<string> ids)
        {
            var requested = ids.Distinct().ToArray();
            var results = requested.ToDictionary(id => id, _ => new List<Edition>());
            var variants = requested.ToDictionary(id => id, id => provider is "hc" or "gr"
                ? BookEditionIdentity.GetStoredEditionProviderIdVariants($"{provider}:{id}")
                : new List<string> { id });
            var requestsByValue = variants.SelectMany(pair => pair.Value.Select(value => (value, pair.Key)))
                .ToLookup(pair => pair.value, pair => pair.Key);
            var values = requestsByValue.Select(group => group.Key).ToArray();
            if (values.Length == 0)
            {
                return results;
            }

            var columns = provider switch
            {
                "hc" => new[] { "ForeignEditionId", "HardcoverEditionId" },
                "gr" => new[] { "ForeignEditionId", "GoodreadsEditionId" },
                "ol" => new[] { "OpenLibraryEditionId" },
                "gb" => new[] { "GoogleBooksEditionId" },
                "az" => new[] { "Asin", "AudibleASIN" },
                _ => Array.Empty<string>()
            };
            if (columns.Length == 0)
            {
                return results;
            }

            var matches = new List<Edition>();
            foreach (var chunk in values.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters / columns.Length : values.Length))
            {
                var terms = new List<string>();
                var parameters = new DynamicParameters();
                foreach (var column in columns)
                {
                    var columnValues = column == "ForeignEditionId"
                        ? chunk.Where(value => value.StartsWith(provider + ":", StringComparison.OrdinalIgnoreCase)).ToArray()
                        : chunk;
                    if (column == "GoodreadsEditionId")
                    {
                        var numeric = columnValues.Select(ProviderIdHelper.StripPrefix)
                            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? (long?)parsed : null)
                            .Where(value => value.HasValue).Select(value => value.Value).Distinct().ToArray();
                        if (numeric.Length == 0)
                        {
                            continue;
                        }
                        parameters.Add(column, numeric);
                    }
                    else
                    {
                        if (columnValues.Length == 0)
                        {
                            continue;
                        }
                        parameters.Add(column, columnValues);
                    }

                    var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? $"= ANY(@{column})" : $"IN @{column}";
                    terms.Add(provider == "az" ? BuildAsinColumnClause(column, inClause) : $"\"{_table}\".\"{column}\" {inClause}");
                }

                if (terms.Count > 0)
                {
                    matches.AddRange(Query(Builder().Where(string.Join(" OR ", terms), parameters)));
                }
            }

            void AddMatches(IEnumerable<Edition> editions)
            {
                foreach (var edition in editions.DistinctBy(edition => edition.Id))
                {
                    var storedIds = provider switch
                    {
                        "hc" => new[] { edition.ForeignEditionId, edition.HardcoverEditionId },
                        "gr" => new[] { edition.ForeignEditionId, edition.GoodreadsEditionId.HasValue ? $"gr:{edition.GoodreadsEditionId.Value}" : null },
                        "ol" => new[] { edition.OpenLibraryEditionId },
                        "gb" => new[] { edition.GoogleBooksEditionId },
                        "az" => new[] { edition.Asin, edition.AudibleASIN },
                        _ => Array.Empty<string>()
                    };
                    foreach (var id in storedIds.Where(value => value != null)
                                 .SelectMany(value => requestsByValue[provider == "az" && _database.DatabaseType == DatabaseType.PostgreSQL ? value.ToUpperInvariant() : value]).Distinct())
                    {
                        if (provider is not ("hc" or "gr") || BookEditionIdentity.EditionMatchesProviderId(edition, $"{provider}:{id}"))
                        {
                            results[id].Add(edition);
                        }
                    }
                }
            }
            AddMatches(matches);

            var aliasValues = requested.Select(id => provider == "az" ? id.ToUpperInvariant()
                : BookEditionIdentity.NormalizeRequestedEditionProviderId($"{provider}:{id}").Substring(provider.Length + 1)).Distinct().ToArray();
            var aliasMatches = new Dictionary<int, Edition>();
            using (var connection = _database.OpenConnection())
            {
                foreach (var chunk in aliasValues.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 1 : aliasValues.Length))
                {
                    var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                    // One indexed read returns matching rows and each complete attested alias set.
                    var sql = $@"SELECT e.*, a.* FROM ""Editions"" e
                        JOIN (SELECT DISTINCT ""EntityId"" FROM ""ProviderAliasIndex""
                            WHERE ""EntityType"" = 'Edition' AND ""Scope"" = 'edition' AND ""Provider"" = @provider
                              AND ""NormalizedProviderId"" {inClause}) requested ON requested.""EntityId"" = e.""Id""
                        JOIN ""ProviderAliasIndex"" a ON a.""EntityType"" = 'Edition' AND a.""Scope"" = 'edition' AND a.""EntityId"" = e.""Id""";
                    connection.Query<Edition, ProviderAlias, Edition>(sql, (edition, alias) =>
                    {
                        if (!aliasMatches.TryGetValue(edition.Id, out var stored))
                        {
                            stored = edition;
                            stored.RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            aliasMatches.Add(stored.Id, stored);
                        }
                        stored.RemoteProviderIds.Add($"{alias.Provider}:{alias.NormalizedProviderId}");
                        return stored;
                    }, new { provider, ids = chunk.ToArray() }, splitOn: "Id").ToList();
                }
            }
            var requestsByAlias = requested.ToLookup(id => provider == "az" ? id.ToUpperInvariant() : BookEditionIdentity.NormalizeRequestedEditionProviderId($"{provider}:{id}").Substring(provider.Length + 1));
            foreach (var edition in aliasMatches.Values)
            {
                foreach (var id in ProviderAliasService.NormalizeAliases(edition.RemoteProviderIds).Where(alias => alias.Provider == provider)
                    .SelectMany(alias => requestsByAlias[alias.NormalizedProviderId]).Distinct())
                {
                    results[id].RemoveAll(match => match.Id == edition.Id);
                    results[id].Add(edition);
                }
            }
            foreach (var key in requested) results[key] = results[key].OrderBy(edition => edition.Id).ToList();
            return results;
        }

        public List<Edition> FindAllByAsin(string asin)
        {
            return FindAllByAsin(asin, null);
        }

        public List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                return new List<Edition>();
            }

            var normalizedAsin = asin.Trim().ToUpperInvariant();
            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Edition)) ?? "Editions";

            // Stage 1: Try exact match on indexed Asin/AudibleASIN columns first (fast path)
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .OrderBy($"\"{tableName}\".\"Id\"");

            builder.Where($"({BuildAsinColumnClause("Asin", "= @asin")} OR {BuildAsinColumnClause("AudibleASIN", "= @asin")})",
                new { asin = normalizedAsin });

            if (mediaType.HasValue)
            {
                builder.Where<Book>(b => b.MediaType == mediaType.Value);
            }

            var results = _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();
            var aliases = _aliasRepository.FindAliases("Edition", "edition", new[] { ("az", normalizedAsin) });
            var ids = aliases.Select(alias => alias.EntityId).Distinct().ToArray();
            if (ids.Length == 0) return results;
            var aliasBuilder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Edition>(edition => ids.Contains(edition.Id));
            if (mediaType.HasValue) aliasBuilder.Where<Book>(book => book.MediaType == mediaType.Value);
            return HydrateEditionAliases(results.Concat(_database.QueryJoined<Edition, Book, Author>(aliasBuilder, MapEditionBookAuthor)).DistinctBy(edition => edition.Id).OrderBy(edition => edition.Id).ToList());
        }

        private string BuildAsinColumnClause(string column, string comparison)
        {
            var reference = $"\"{_table}\".\"{column}\"";
            var expression = _database.DatabaseType == DatabaseType.PostgreSQL ? $"UPPER({reference})" : reference;
            // Both providers require the partial-index predicates to make their existing indexes eligible.
            return $"({reference} IS NOT NULL AND LENGTH({reference}) > 0 AND {expression} {comparison})";
        }

        private static Edition MapEditionBookAuthor(Edition edition, Book book, Author author)
        {
            if (book != null)
            {
                book.Author = author;
                edition.Book = book;
            }

            return edition;
        }

        public Edition FindByIsbn(string isbn)
        {
            return ChooseOldest(FindAllByIsbn(isbn), nameof(Edition.Isbn13), isbn, expectedPlural: true);
        }

        public List<Edition> FindAllByIsbn(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return new List<Edition>();
            }

            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Edition)) ?? "Editions";
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Edition>(e => e.Isbn13 == isbn)
                .OrderBy($"\"{tableName}\".\"Id\"");

            var editions = _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();

            if (editions.Any())
            {
                return editions;
            }

            builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Edition>(e => e.Isbn10 == isbn)
                .OrderBy($"\"{tableName}\".\"Id\"");

            return _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();

        }
    }
}
