using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Books
{
    // Identity repair uses the caller's library transaction. It never moves files, selects
    // additional books, or publishes deletion events (which would discard acquisition state).
    internal sealed class LibraryIdentityWriter
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly IDbConnection _connection;
        private readonly IDbTransaction _transaction;
        private readonly DatabaseType _databaseType;
        private readonly IEditionSelector _selector;
        // Bounded by this transaction's affected authors/jobs. Reuse parsed acquisition
        // targets across a batch of merges instead of re-reading each author's queue per row.
        private readonly Dictionary<string, Dictionary<long, IDictionary<string, object>>> _targetRows = new();
        private readonly Dictionary<string, HashSet<int>> _loadedTargetOwners = new();
        private readonly Dictionary<(string Table, long Id, string Column), JsonNode> _targetJson = new();
        private List<ConversionTarget> _conversionTargets;
        internal int TargetReadCount { get; private set; }


        public LibraryIdentityWriter(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, IEditionSelector selector)
        {
            _connection = connection;
            _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _databaseType = databaseType;
            _selector = selector;
        }

        private int Execute(string sql, object args = null) => _connection.Execute(sql, args, _transaction);
        private List<T> Query<T>(string sql, object args = null) => _connection.Query<T>(sql, args, _transaction).ToList();
        private string In(string parameter) => _databaseType == DatabaseType.PostgreSQL ? $"= ANY(@{parameter})" : $"IN @{parameter}";

        public bool MergeBook(int sourceId, int targetId)
        {
            var timer = Stopwatch.StartNew();
            if (sourceId == targetId) return false;
            var books = Query<Book>("SELECT * FROM \"Books\" WHERE \"Id\" = @sourceId OR \"Id\" = @targetId", new { sourceId, targetId });
            var source = books.SingleOrDefault(book => book.Id == sourceId);
            var target = books.SingleOrDefault(book => book.Id == targetId);
            if (source == null || target == null)
            {
                Logger.Warn("[BOOK-MERGE] Skipped {0} -> {1}: {2} book row is already missing; no remaining rows changed.", sourceId, targetId, source == null ? "source" : "target");
                return false;
            }
            if (source.MediaType != target.MediaType) throw new InvalidOperationException("Cannot merge books across media types.");

            var editions = Query<Edition>("SELECT * FROM \"Editions\" WHERE \"BookId\" = @sourceId OR \"BookId\" = @targetId", new { sourceId, targetId });
            source.Editions = editions.Where(edition => edition.BookId == sourceId).ToList();
            var populated = Query<int>(@"SELECT DISTINCT e.""BookId"" FROM ""Editions"" e
                JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                WHERE e.""BookId"" = @sourceId OR e.""BookId"" = @targetId", new { sourceId, targetId });
            if (!BookIdentity.CanAbsorbDuplicate(source, populated.Contains(sourceId)))
                throw new InvalidOperationException("A populated or intentional book copy must keep its local identity; only an unpinned fileless duplicate may be absorbed.");
            var targets = editions.Where(edition => edition.BookId == targetId).ToList();
            var editionMap = new Dictionary<int, int>();
            // Release the source's selection before moving it into a pocket with its own selection.
            Execute("UPDATE \"Editions\" SET \"Monitored\" = @off WHERE \"BookId\" = @sourceId", new { sourceId, off = false });
            foreach (var edition in editions.Where(edition => edition.BookId == sourceId))
            {
                var equivalent = FindEquivalent(edition, targets);
                if (equivalent == null)
                {
                    Execute("UPDATE \"Editions\" SET \"BookId\" = @targetId WHERE \"Id\" = @Id", new { targetId, edition.Id });
                    edition.BookId = targetId;
                    targets.Add(edition);
                }
                else
                {
                    MergeEdition(edition.Id, equivalent.Id);
                    equivalent.ManualAdd |= edition.ManualAdd;
                    equivalent.Monitored |= edition.Monitored;
                    editionMap[edition.Id] = equivalent.Id;
                }
            }

            // Author monitoring is separate: only retain monitoring that this merged pair already had.
            Execute(@"UPDATE ""Books"" SET ""AudiobookMonitored"" = @audio, ""EbookMonitored"" = @ebook
                      WHERE ""Id"" = @targetId", new { targetId, audio = source.AudiobookMonitored || target.AudiobookMonitored, ebook = source.EbookMonitored || target.EbookMonitored });
            foreach (var table in new[] { "History", "DownloadHistory", "MetadataFiles", "ExtraFiles" })
                Execute($"UPDATE \"{table}\" SET \"BookId\" = @targetId, \"AuthorId\" = @authorId WHERE \"BookId\" = @sourceId", new { sourceId, targetId, authorId = target.AuthorId });
            Execute("UPDATE \"OtherFiles\" SET \"AuthorId\" = @authorId WHERE \"EditionId\" IN (SELECT \"Id\" FROM \"Editions\" WHERE \"BookId\" = @targetId)", new { targetId, authorId = target.AuthorId });
            Execute("UPDATE \"ABSLookupHistory\" SET \"BookId\" = @targetId WHERE \"BookId\" = @sourceId", new { sourceId, targetId });
            MergeLinks("SeriesBookLink", "BookId", new[] { "SeriesId", "SeriesInstanceType" }, sourceId, targetId);
            MergeLinks("BookNarratorLink", "BookId", new[] { "NarratorId" }, sourceId, targetId);
            RewriteTargets(new Dictionary<int, int> { [sourceId] = targetId }, editionMap, new[] { source.AuthorId, target.AuthorId }, target.AuthorId);
            SelectEdition(target, targets);
            Execute("DELETE FROM \"Books\" WHERE \"Id\" = @sourceId", new { sourceId });
            Logger.Debug("[BOOK-MERGE] {0} -> {1}: {2} editions, {3} equivalent editions, {4} acquisition reads in this transaction, {5} ms", sourceId, targetId, editions.Count, editionMap.Count, TargetReadCount, timer.ElapsedMilliseconds);
            return true;
        }

        private static Edition FindEquivalent(Edition source, List<Edition> targets)
        {
            var stable = BookEditionIdentity.GetStableEditionRehomeTokens(source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var amazon = BookEditionIdentity.GetAmazonEditionRehomeTokens(source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = targets.Where(target => stable.Count > 0
                ? stable.Overlaps(BookEditionIdentity.GetStableEditionRehomeTokens(target))
                : amazon.Overlaps(BookEditionIdentity.GetRemoteEditionRehomeTokens(target))).ToList();
            // Ambiguous provider evidence preserves the edition rather than guessing its recording.
            return matches.Count == 1 ? matches[0] : null;
        }

        private void SelectEdition(Book target, List<Edition> editions)
        {
            var counts = Query<(int EditionId, int Count)>(@"SELECT f.""EditionId"", COUNT(*) AS ""Count"" FROM ""BookFiles"" f
                JOIN ""Editions"" e ON e.""Id"" = f.""EditionId"" WHERE e.""BookId"" = @Id GROUP BY f.""EditionId""", new { target.Id })
                .ToDictionary(row => row.EditionId, row => row.Count);
            _selector.EnsureSingleMonitoredEdition(editions, counts, target.MediaType, target.AnyEditionOk);
            Execute("UPDATE \"Editions\" SET \"Monitored\" = @off WHERE \"BookId\" = @Id", new { target.Id, off = false });
            foreach (var edition in editions.Where(edition => edition.Monitored || edition.ManualAdd))
                Execute("UPDATE \"Editions\" SET \"Monitored\" = @Monitored, \"ManualAdd\" = @ManualAdd WHERE \"Id\" = @Id", edition);
            Execute("UPDATE \"Books\" SET \"ForeignEditionId\" = @foreignId WHERE \"Id\" = @Id",
                new { target.Id, foreignId = editions.SingleOrDefault(edition => edition.Monitored)?.ForeignEditionId });
        }

        public void MergeEdition(int sourceId, int targetId)
        {
            if (sourceId == targetId) return;
            var rows = Query<(int Id, int BookId, int MediaType)>(@"SELECT e.""Id"", e.""BookId"", b.""MediaType""
                FROM ""Editions"" e JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                WHERE e.""Id"" = @sourceId OR e.""Id"" = @targetId", new { sourceId, targetId });
            if (rows.Count != 2)
                throw new InvalidOperationException($"Edition merge {sourceId} -> {targetId} cannot proceed: an edition or its book is missing; retry with fresh metadata.");
            if (rows[0].MediaType != rows[1].MediaType)
                throw new InvalidOperationException("Cannot merge editions across media types.");
            foreach (var table in new[] { "BookFiles", "History", "OtherFiles", "narrator_normalized" })
                Execute($"UPDATE \"{table}\" SET \"EditionId\" = @targetId WHERE \"EditionId\" = @sourceId", new { sourceId, targetId });
            MergeLinks("EditionNarratorLink", "EditionId", new[] { "NarratorId" }, sourceId, targetId);
            Execute(@"UPDATE ""Editions"" SET ""ManualAdd"" = CASE WHEN ""ManualAdd"" = @on OR
                      EXISTS (SELECT 1 FROM ""Editions"" WHERE ""Id"" = @sourceId AND ""ManualAdd"" = @on) THEN @on ELSE @off END
                      WHERE ""Id"" = @targetId", new { sourceId, targetId, on = true, off = false });
            // The enclosing operation supplies the book mapping; this handles same-pocket edition dedup too.
            var owners = Query<int>(@"SELECT DISTINCT b.""AuthorId"" FROM ""Books"" b JOIN ""Editions"" e ON e.""BookId"" = b.""Id""
                                     WHERE e.""Id"" = @sourceId OR e.""Id"" = @targetId", new { sourceId, targetId });
            RewriteTargets(new Dictionary<int, int>(), new Dictionary<int, int> { [sourceId] = targetId }, owners, null);
            Execute("DELETE FROM \"Editions\" WHERE \"Id\" = @sourceId", new { sourceId });
        }

        internal sealed record EditionMove(int EditionId, int TargetBookId, int? EquivalentEditionId = null);

        public List<int> MoveEditions(IReadOnlyCollection<EditionMove> moves)
        {
            if (moves.Count == 0) return new List<int>();
            var sources = moves.Select(move => move.EditionId).Distinct().Chunk(400)
                .SelectMany(ids => Query<Edition>($"SELECT * FROM \"Editions\" WHERE \"Id\" {In("ids")}", new { ids })).ToDictionary(edition => edition.Id);
            var books = sources.Values.Select(edition => edition.BookId).Concat(moves.Select(move => move.TargetBookId)).Distinct().Chunk(400)
                .SelectMany(ids => Query<Book>($"SELECT * FROM \"Books\" WHERE \"Id\" {In("ids")}", new { ids })).ToDictionary(book => book.Id);
            // Validate the complete plan before the first write. Missing evidence aborts the
            // caller's transaction; it must never leave a file pointing at a missing edition.
            if (moves.Any(move => !sources.ContainsKey(move.EditionId) || !books.ContainsKey(move.TargetBookId)) ||
                sources.Values.Any(edition => !books.ContainsKey(edition.BookId)))
                throw new InvalidOperationException("Edition ownership changed while preparing the move; an edition or book is missing. Retry with fresh metadata.");
            var allEditions = books.Keys.Chunk(400).SelectMany(ids => Query<Edition>($"SELECT * FROM \"Editions\" WHERE \"BookId\" {In("ids")}", new { ids })).ToList();
            var byEdition = allEditions.ToDictionary(edition => edition.Id);
            foreach (var move in moves)
            {
                if (books[sources[move.EditionId].BookId].MediaType != books[move.TargetBookId].MediaType)
                    throw new InvalidOperationException("Cannot rehome editions across media types.");
                if (move.EquivalentEditionId is int equivalent &&
                    (!byEdition.TryGetValue(equivalent, out var targetEdition) || targetEdition.BookId != move.TargetBookId))
                    throw new InvalidOperationException($"Equivalent edition {equivalent} is missing or belongs to a different target; retry with fresh metadata.");
            }
            var filedEditions = books.Keys.Chunk(400).SelectMany(ids => Query<int>($@"SELECT DISTINCT f.""EditionId"" FROM ""BookFiles"" f
                JOIN ""Editions"" e ON e.""Id"" = f.""EditionId"" WHERE e.""BookId"" {In("ids")}", new { ids })).ToHashSet();
            var populated = allEditions.Where(edition => filedEditions.Contains(edition.Id)).Select(edition => edition.BookId).ToHashSet();
            var editionsByBook = allEditions.ToLookup(edition => edition.BookId);
            foreach (var book in books.Values) book.Editions = editionsByBook[book.Id].ToList();
            var absorbable = books.Values.ToDictionary(book => book.Id, book => BookIdentity.CanAbsorbDuplicate(book, populated.Contains(book.Id)));
            var deleted = new List<int>();
            // Identity changes belong to the refresh planner's UseMetadataFrom write.
            // An entire populated origin must keep its row/settings and adopt the served
            // pocket there; a low-level move is only for a genuine partial split.
            foreach (var group in moves.Where(move => sources[move.EditionId].BookId != move.TargetBookId)
                .GroupBy(move => sources[move.EditionId].BookId))
            {
                var movedIds = group.Select(move => move.EditionId).ToHashSet();
                if (populated.Contains(group.Key) && editionsByBook[group.Key].All(edition => movedIds.Contains(edition.Id)))
                    throw new InvalidOperationException($"Cannot empty populated book {group.Key}; reconcile its served identity before moving editions.");
            }
            var retainedSelections = new Dictionary<int, (bool Monitored, bool Pinned)>();
            var destinations = new Dictionary<int, HashSet<int>>();
            foreach (var group in moves.GroupBy(move => books[move.TargetBookId].AuthorId))
                RewriteTargets(new Dictionary<int, int>(), new Dictionary<int, int>(), books.Values.Select(book => book.AuthorId), group.Key,
                    group.ToDictionary(move => move.EditionId, move => (sources[move.EditionId].BookId, move.TargetBookId)));
            foreach (var move in moves)
            {
                var source = sources[move.EditionId];
                var origin = books[source.BookId];
                if (!destinations.TryGetValue(origin.Id, out var targets)) destinations[origin.Id] = targets = new HashSet<int>();
                targets.Add(move.TargetBookId);
                var target = books[move.TargetBookId];
                if (origin.MediaType != target.MediaType) throw new InvalidOperationException("Cannot rehome editions across media types.");
                var args = new { sourceId = source.Id, sourceBook = origin.Id, targetId = target.Id, authorId = target.AuthorId };
                // Edition-specific history is proof. Unattributable history remains on its original
                // pocket until there is evidence to move it; no arbitrary destination is chosen.
                Execute(@"UPDATE ""DownloadHistory"" SET ""BookId"" = @targetId, ""AuthorId"" = @authorId
                          WHERE ""BookId"" = @sourceBook AND ""DownloadId"" IN
                          (SELECT ""DownloadId"" FROM ""History"" WHERE ""EditionId"" = @sourceId)
                          AND NOT EXISTS (SELECT 1 FROM ""History"" h WHERE h.""DownloadId"" = ""DownloadHistory"".""DownloadId""
                              AND h.""BookId"" = @sourceBook AND h.""EditionId"" > 0 AND h.""EditionId"" <> @sourceId)", args);
                Execute("UPDATE \"History\" SET \"BookId\" = @targetId, \"AuthorId\" = @authorId WHERE \"EditionId\" = @sourceId", args);
                foreach (var table in new[] { "MetadataFiles", "ExtraFiles" })
                    Execute($"UPDATE \"{table}\" SET \"BookId\" = @targetId, \"AuthorId\" = @authorId WHERE \"BookFileId\" IN (SELECT \"Id\" FROM \"BookFiles\" WHERE \"EditionId\" = @sourceId)", args);
                Execute("UPDATE \"OtherFiles\" SET \"AuthorId\" = @authorId WHERE \"EditionId\" = @sourceId", args);
                if (move.EquivalentEditionId.HasValue)
                    MergeEdition(source.Id, move.EquivalentEditionId.Value);
                else
                    Execute("UPDATE \"Editions\" SET \"BookId\" = @targetId, \"Monitored\" = @off WHERE \"Id\" = @sourceId", new { sourceId = source.Id, targetId = target.Id, off = false });
                var selectedId = move.EquivalentEditionId ?? source.Id;
                var selected = retainedSelections.GetValueOrDefault(selectedId);
                retainedSelections[selectedId] = (selected.Monitored || source.Monitored, selected.Pinned || source.ManualAdd);
            }
            var current = books.Keys.Chunk(400).SelectMany(ids => Query<Edition>($"SELECT * FROM \"Editions\" WHERE \"BookId\" {In("ids")}", new { ids })).ToLookup(edition => edition.BookId);
            foreach (var book in books.Values)
            {
                var candidates = current[book.Id].ToList();
                foreach (var edition in candidates)
                    if (retainedSelections.TryGetValue(edition.Id, out var selection))
                    {
                        edition.Monitored |= selection.Monitored;
                        edition.ManualAdd |= selection.Pinned;
                    }
                if (candidates.Count > 0) SelectEdition(book, candidates);
            }
            foreach (var pair in destinations.Where(pair => pair.Value.Count == 1))
            {
                var source = books[pair.Key];
                if (current[source.Id].Any() || !absorbable[source.Id]) continue;
                // Every edition went to one proven pocket: its remaining acquisition state has
                // the same unambiguous destination. Multi-destination history is kept, not guessed.
                if (MergeBook(source.Id, pair.Value.Single())) deleted.Add(source.Id);
            }
            return deleted;
        }

        public void ReassignBooks(IReadOnlyCollection<int> bookIds, int authorId, int? expectedAuthorId = null)
        {
            foreach (var ids in bookIds.Distinct().Chunk(400))
            {
                var owners = Query<int>($"SELECT DISTINCT \"AuthorId\" FROM \"Books\" WHERE \"Id\" {In("ids")}", new { ids });
                if (expectedAuthorId.HasValue && owners.Any(owner => owner != expectedAuthorId.Value))
                    throw new InvalidOperationException("Book ownership changed while the split was being prepared; retry with fresh metadata.");
                foreach (var table in new[] { "History", "DownloadHistory", "MetadataFiles", "ExtraFiles" })
                    Execute($"UPDATE \"{table}\" SET \"AuthorId\" = @authorId WHERE \"BookId\" {In("ids")}", new { ids, authorId });
                Execute($"UPDATE \"OtherFiles\" SET \"AuthorId\" = @authorId WHERE \"EditionId\" IN (SELECT \"Id\" FROM \"Editions\" WHERE \"BookId\" {In("ids")})", new { ids, authorId });
                Execute($"UPDATE \"Books\" SET \"AuthorId\" = @authorId WHERE \"Id\" {In("ids")}", new { ids, authorId });
                RewriteTargets(ids.ToDictionary(id => id, id => id), new Dictionary<int, int>(), owners, authorId);
            }
        }

        private void MergeLinks(string table, string column, string[] identity, int sourceId, int targetId)
        {
            var predicate = string.Join(" AND ", identity.Select(key => $"(t.\"{key}\" = \"{table}\".\"{key}\" OR (t.\"{key}\" IS NULL AND \"{table}\".\"{key}\" IS NULL))"));
            Execute($"DELETE FROM \"{table}\" WHERE \"{column}\" = @sourceId AND EXISTS (SELECT 1 FROM \"{table}\" t WHERE t.\"{column}\" = @targetId AND {predicate})", new { sourceId, targetId });
            Execute($"UPDATE \"{table}\" SET \"{column}\" = @targetId WHERE \"{column}\" = @sourceId", new { sourceId, targetId });
        }

        private IEnumerable<IDictionary<string, object>> LoadTargets(string table, int[] owners)
        {
            if (!_targetRows.TryGetValue(table, out var rows)) _targetRows[table] = rows = new();
            if (!_loadedTargetOwners.TryGetValue(table, out var loaded)) _loadedTargetOwners[table] = loaded = new();
            foreach (var missing in owners.Where(owner => !loaded.Contains(owner)).Chunk(400))
            {
                TargetReadCount++;
                foreach (IDictionary<string, object> row in Query<dynamic>($"SELECT * FROM \"{table}\" WHERE \"AuthorId\" {In("missing")}", new { missing }))
                    rows[Convert.ToInt64(row["Id"])] = row;
                loaded.UnionWith(missing);
            }
            var selected = owners.ToHashSet();
            return rows.Values.Where(row => selected.Contains(Convert.ToInt32(row["AuthorId"])));
        }

        private JsonNode TargetJson(string table, IDictionary<string, object> row, string column)
        {
            var key = (table, Convert.ToInt64(row["Id"]), column);
            if (!_targetJson.TryGetValue(key, out var node))
                _targetJson[key] = node = row[column] is string json && !string.IsNullOrWhiteSpace(json) ? JsonNode.Parse(json) : null;
            return node;
        }

        private void RewriteTargets(Dictionary<int, int> books, Dictionary<int, int> editions, IEnumerable<int> authorIds, int? targetAuthorId, Dictionary<int, (int Source, int Target)> movedEditions = null)
        {
            var owners = authorIds.Distinct().ToArray();
            if (owners.Length == 0) return;
            foreach (var table in new[] { "Blacklist", "PendingReleases" })
            {
                var columns = table == "Blacklist" ? new[] { "BookIds" } : new[] { "AdditionalInfo", "ParsedBookInfo" };
                foreach (var values in LoadTargets(table, owners))
                {
                    var rowBooks = books;
                    if (movedEditions != null)
                    {
                        if (table != "PendingReleases" || values["AdditionalInfo"] is not string info || string.IsNullOrWhiteSpace(info)) continue;
                        var recorded = TargetJson(table, values, "AdditionalInfo") as JsonObject;
                        var selected = recorded?.FirstOrDefault(property => property.Key.Equals("targetEditionIds", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
                        if (selected == null) continue;
                        rowBooks = new Dictionary<int, int>();
                        foreach (var selection in selected)
                            if (selection.Value is JsonValue value && value.TryGetValue<int>(out var editionId) &&
                                movedEditions.TryGetValue(editionId, out var move) && selection.Key == move.Source.ToString())
                                rowBooks[move.Source] = move.Target;
                        if (rowBooks.Count == 0) continue;
                    }
                    var changed = false;
                    var args = new DynamicParameters(new { id = (int)(long)Convert.ToInt64(values["Id"]), authorId = values["AuthorId"] });
                    var assignments = new List<string>();
                    foreach (var column in columns)
                    {
                        var json = values[column] as string;
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var node = TargetJson(table, values, column);
                        var bookIds = new HashSet<int>();
                        var columnChanged = Rewrite(node, column, rowBooks, editions, bookIds);
                        // A pack spanning both authors remains attached to its original author;
                        // its per-book targets remain intact instead of inventing whole-pack ownership.
                        if (targetAuthorId.HasValue && bookIds.Count > 0 && bookIds.All(rowBooks.ContainsKey))
                        {
                            args.Add("authorId", targetAuthorId.Value);
                            values["AuthorId"] = targetAuthorId.Value;
                            changed = true;
                        }
                        if (!columnChanged) continue;
                        args.Add(column, node.ToJsonString());
                        assignments.Add($"\"{column}\" = @{column}");
                        changed = true;
                    }
                    if (!changed) continue;
                    assignments.Add("\"AuthorId\" = @authorId");
                    Execute($"UPDATE \"{table}\" SET {string.Join(", ", assignments)} WHERE \"Id\" = @id", args);
                }
            }

            // Conversion requests use a book id, not an edition id. Restrict to unfinished jobs.
            if (books.Count == 0) return;
            if (_conversionTargets == null)
            {
                TargetReadCount++;
                _conversionTargets = Query<ConversionTarget>(@"SELECT ""Id"", ""RequestJson"" FROM ""ConversionJobs"" WHERE ""Status"" IN (0, 1, 2, 4, 5, 6)");
            }
            foreach (var row in _conversionTargets)
            {
                if (string.IsNullOrWhiteSpace(row.RequestJson)) continue;
                var node = row.Parsed ??= JsonNode.Parse(row.RequestJson);
                if (Rewrite(node, "", books, editions, new HashSet<int>()))
                {
                    var job = new ConversionJob { Id = row.Id, RequestJson = node.ToJsonString() };
                    job.PrepareForPersistence(new[] { nameof(ConversionJob.RequestJson) });
                    Execute(@"UPDATE ""ConversionJobs"" SET ""RequestJson"" = @RequestJson,
                        ""CleanupPending"" = @CleanupPending, ""CleanupAttention"" = @CleanupAttention WHERE ""Id"" = @Id", job);
                }
            }
        }

        private sealed class ConversionTarget { public int Id { get; set; } public string RequestJson { get; set; } public JsonNode Parsed { get; set; } }

        private static bool Rewrite(JsonNode node, string name, Dictionary<int, int> books, Dictionary<int, int> editions, HashSet<int> bookIds)
        {
            var changed = false;
            if (node is JsonArray array && name.Equals("bookIds", StringComparison.OrdinalIgnoreCase))
            {
                var originals = array.Select(value => value.GetValue<int>()).ToList();
                bookIds.UnionWith(originals);
                var ids = originals.Select(id => books.GetValueOrDefault(id, id)).Distinct().ToList();
                if (originals.SequenceEqual(ids)) return false;
                array.Clear();
                foreach (var id in ids) array.Add(id);
                return true;
            }
            if (node is not JsonObject obj) return false;
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue scalar && scalar.TryGetValue<int>(out var id))
                {
                    var map = property.Key.Equals("bookId", StringComparison.OrdinalIgnoreCase) ? books
                        : property.Key.Equals("editionId", StringComparison.OrdinalIgnoreCase) ? editions : null;
                    if (map == books) bookIds.Add(id);
                    if (map != null && map.TryGetValue(id, out var target) && target != id) { obj[property.Key] = target; changed = true; }
                }
                else if (property.Value is JsonObject targets && (property.Key.Equals("targetEditionIds", StringComparison.OrdinalIgnoreCase) || property.Key.Equals("targetCustomFormatScores", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in targets.ToList())
                    {
                        if (!int.TryParse(pair.Key, out var bookId)) continue;
                        bookIds.Add(bookId);
                        var targetBook = books.GetValueOrDefault(bookId, bookId).ToString();
                        var value = pair.Value.GetValue<int>();
                        if (property.Key.Equals("targetEditionIds", StringComparison.OrdinalIgnoreCase)) value = editions.GetValueOrDefault(value, value);
                        if (targetBook == pair.Key && value == pair.Value.GetValue<int>()) continue;
                        targets.Remove(pair.Key);
                        // Keep the survivor's existing acquisition preference on a collision.
                        if (!targets.ContainsKey(targetBook)) targets[targetBook] = value;
                        changed = true;
                    }
                }
                else changed |= Rewrite(property.Value, property.Key, books, editions, bookIds);
            }
            return changed;
        }
    }
}
