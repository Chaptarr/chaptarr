using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dapper;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileRepository : IBasicRepository<BookFile>
    {
        List<BookFile> GetFilesByAuthor(int authorId);
        List<BookFile> GetFilePathOwnership(IReadOnlyCollection<int> authorIds, IReadOnlyCollection<string> folders);
        List<BookFile> GetMappedFilePathEvidenceByAuthor(int authorId, string mediaType);
        List<BookFile> GetFilesByBook(int bookId);
        List<BookFile> GetFilesByBooks(List<int> bookIds);
        List<BookFile> GetFilesByEdition(int editionId);
        List<BookFile> GetUnmappedFiles();
        List<BookFile> GetUnmappedFiles(string mediaType);
        List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType);
        List<BookFile> GetFilesWithBasePath(string path);
        List<BookFile> GetFilesWithBasePath(string path, string mediaType);
        HashSet<string> GetReplicaPathsWithBasePath(string path);
        List<BookFile> GetFileWithPath(List<string> paths);
        BookFile GetFileWithPath(string path);
        int InsertManyIgnoreDuplicatePaths(List<BookFile> files);
        void ReplaceMany(List<BookFile> filesToAdd, List<BookFile> filesToDelete);
        void ReplaceMany(List<BookFile> filesToAdd, List<BookFile> filesToDelete, IDbConnection connection, IDbTransaction transaction);
        (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, AudioProductionCorrection correction, IDbConnection connection, IDbTransaction transaction);
    }

    public class MediaFileRepository : BasicRepository<BookFile>, IMediaFileRepository
    {
        public MediaFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        // always join with all the other good stuff
        // needed more often than not so better to load it all now
        protected override SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType)
            .LeftJoin<BookFile, Edition>((b, e) => b.EditionId == e.Id)
            .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
            .LeftJoin<Book, Author>((book, author) => book.AuthorId == author.Id);

        // For lookups that filter on the book or its author. Those can only match files that
        // have an edition and a book, so the INNER JOINs return the same rows as Builder().
        // They also let the database start from the book or author and follow the indexes
        // down to the files. With LEFT JOINs, SQLite has to read every BookFiles row to
        // answer a lookup for one book or author.
        private SqlBuilder MappedFilesBuilder() => new SqlBuilder(_database.DatabaseType)
            .Join<BookFile, Edition>((b, e) => b.EditionId == e.Id)
            .Join<Edition, Book>((e, b) => e.BookId == b.Id)
            .LeftJoin<Book, Author>((book, author) => book.AuthorId == author.Id);

        protected override List<BookFile> Query(SqlBuilder builder) => Query(_database, builder).ToList();

        public static IEnumerable<BookFile> Query(IDatabase database, SqlBuilder builder)
        {
            return database.QueryJoined<BookFile, Edition, Book, Author>(builder, (file, edition, book, author) => Map(file, edition, book, author));
        }

        private static BookFile Map(BookFile file, Edition edition, Book book, Author author)
        {
            file.Edition = edition;

            if (edition != null)
            {
                edition.Book = book;

                if (book != null)
                {
                    book.Author = author;
                }
            }

            file.Author = author;

            return file;
        }

        public List<BookFile> GetFilesByAuthor(int authorId)
        {
            return Query(MappedFilesBuilder().Where<Book>(b => b.AuthorId == authorId));
        }

        public List<BookFile> GetFilePathOwnership(IReadOnlyCollection<int> authorIds, IReadOnlyCollection<string> folders)
        {
            // Include replicas even when their primary is elsewhere. SQL scopes the inventory;
            // callers still use platform path equality before authorizing any physical deletion.
            if (authorIds.Count == 0 && folders.Count == 0) return new List<BookFile>();
            if (authorIds.Count == 0 && folders.All(ConversionWorkPath.IsWorkPath))
            {
                using var ownershipConnection = _database.OpenConnection();
                // The ASCII marker is case-insensitive on every supported platform. The partial
                // index excludes the ordinary library; platform path checks still authorize removal.
                return ownershipConnection.Query<BookFile>(@"SELECT ""Id"", ""Path"", ""ReplicaPaths"" FROM ""BookFiles""
                    WHERE LOWER(""Path"") LIKE '%chaptarr-conversions%' OR LOWER(""ReplicaPaths"") LIKE '%chaptarr-conversions%'").ToList();
            }
            var postgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var authorRows = postgres ? "jsonb_array_elements_text(CAST(@authorIds AS jsonb))" : "json_each(@authorIds)";
            var folderRows = postgres ? "jsonb_array_elements_text(CAST(@folders AS jsonb))" : "json_each(@folders)";
            var authorValue = postgres ? "CAST(a.value AS integer)" : "a.value";
            var scopedFolders = folders.TopmostFolders();
            var collation = postgres ? "\"C\"" : "BINARY";
            // Bound primary paths by binary prefix ranges; replicas require their own thin
            // inventory because their primary may live outside the selected directories.
            var primary = OsInfo.IsWindows ? @"SELECT ""Id"" FROM ""BookFiles""" : $@"
                SELECT bf.""Id"" FROM {folderRows} p JOIN ""BookFiles"" bf ON
                    bf.""Path"" COLLATE {collation} >= (p.value || '/') COLLATE {collation}
                    AND bf.""Path"" COLLATE {collation} < (p.value || '0') COLLATE {collation}
                UNION SELECT bf.""Id"" FROM {folderRows} p JOIN ""BookFiles"" bf ON bf.""Path"" = p.value";
            using var connection = _database.OpenConnection();
            var files = connection.Query<BookFile, Author, BookFile>(
                $@"SELECT bf.""Id"", bf.""Path"", bf.""ReplicaPaths"", bf.""CalibreId"", bf.""EditionId"", bf.""Size"",
                         COALESCE(NULLIF(bf.""MediaType"", ''), CASE b.""MediaType"" WHEN @audio THEN 'audiobook' WHEN @ebook THEN 'ebook' END) AS ""MediaType"",
                         b.""AuthorId"" AS ""Id"", owner.""Name""
                  FROM ""BookFiles"" bf
                  LEFT JOIN ""Editions"" e ON e.""Id"" = bf.""EditionId""
                  LEFT JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                  LEFT JOIN ""Authors"" owner ON owner.""Id"" = b.""AuthorId""
                  WHERE bf.""Id"" IN ({primary}
                     UNION SELECT bf.""Id"" FROM ""BookFiles"" bf
                         WHERE bf.""ReplicaPaths"" IS NOT NULL AND bf.""ReplicaPaths"" <> '' AND bf.""ReplicaPaths"" <> '[]'
                     UNION SELECT bf.""Id"" FROM ""BookFiles"" bf
                         JOIN ""Editions"" e ON e.""Id"" = bf.""EditionId""
                         JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                         WHERE b.""AuthorId"" IN (SELECT {authorValue} FROM {authorRows} a))",
                (file, author) => { file.Author = author; return file; },
                new { authorIds = JsonSerializer.Serialize(authorIds), folders = JsonSerializer.Serialize(scopedFolders.Select(folder => folder.TrimEnd(Path.DirectorySeparatorChar))), audio = (int)BookMediaType.Audiobook, ebook = (int)BookMediaType.Ebook },
                splitOn: "Id").ToList();

            // SQLite lower() cannot reproduce Windows Unicode path equality. Read only
            // the ownership projection there and let the same platform path rule decide.
            var selectedAuthors = authorIds.ToHashSet();
            return files.Where(file => selectedAuthors.Contains(file.Author?.Id ?? 0) ||
                (file.ReplicaPaths ?? new List<string>()).Append(file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Any(path => scopedFolders.Any(folder => folder.PathEquals(path) || folder.IsParentPath(path)))).ToList();
        }

        public List<BookFile> GetMappedFilePathEvidenceByAuthor(int authorId, string mediaType)
        {
            var requestedMediaType = (mediaType ?? "all").Trim().ToLowerInvariant();
            var mediaTypeClause = requestedMediaType is "audiobook" or "ebook"
                ? @" AND bf.""MediaType"" = @mediaType"
                : string.Empty;

            using var conn = _database.OpenConnection();
            return conn.Query<BookFile>(
                @"SELECT bf.""Path"", bf.""MediaType"", bf.""EditionId""
                  FROM ""BookFiles"" bf
                  INNER JOIN ""Editions"" e ON e.""Id"" = bf.""EditionId""
                  INNER JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                  WHERE b.""AuthorId"" = @authorId
                    AND bf.""EditionId"" > 0" + mediaTypeClause + ";",
                new { authorId, mediaType = requestedMediaType }).ToList();
        }

        public List<BookFile> GetFilesByBook(int bookId)
        {
            // Keep the joined-book predicate for ownership semantics and add the edition predicate for selectivity.
            return Query(MappedFilesBuilder()
                .Where<Edition>(e => e.BookId == bookId)
                .Where<Book>(b => b.Id == bookId));
        }

        public List<BookFile> GetFilesByBooks(List<int> bookIds)
        {
            if (bookIds == null || bookIds.Count == 0)
            {
                return new List<BookFile>();
            }

            var uniqueBookIds = bookIds.Distinct().ToArray();

            List<BookFile> QueryForBookIds(int[] chunkBookIds)
            {
                var builder = new SqlBuilder(_database.DatabaseType)
                    .LeftJoin<BookFile, Edition>((f, e) => f.EditionId == e.Id)
	                    .Where<Edition>(e => Enumerable.Contains(chunkBookIds, e.BookId));

                return _database.QueryJoined<BookFile, Edition>(builder, MapTrack).ToList();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && uniqueBookIds.Length > SqliteVariableLimit.MaxParameters)
            {
                var files = new List<BookFile>();
                foreach (var batch in uniqueBookIds.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    files.AddRange(QueryForBookIds(batch.ToArray()));
                }

                return files.DistinctBy(f => f.Id).ToList();
            }

            return QueryForBookIds(uniqueBookIds);
        }

        public List<BookFile> GetFilesByEdition(int editionId)
        {
            return Query(Builder().Where<BookFile>(f => f.EditionId == editionId));
        }

        public List<BookFile> GetUnmappedFiles()
        {
            return GetUnmappedFiles(null);
        }

        public List<BookFile> GetUnmappedFiles(string mediaType)
        {
            var builder = BuildUnmappedFilesBuilder(mediaType);
            return _database.Query<BookFile>(builder).ToList();
        }

        public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
        {
            if (ids == null)
            {
                return new List<BookFile>();
            }

            var uniqueIds = ids.Where(id => id > 0).Distinct().ToArray();
            if (uniqueIds.Length == 0)
            {
                return new List<BookFile>();
            }

            List<BookFile> QueryForIds(int[] chunkIds)
            {
                var builder = BuildUnmappedFilesBuilder(mediaType)
                    .Where<BookFile>(t => Enumerable.Contains(chunkIds, t.Id));

                return _database.Query<BookFile>(builder).ToList();
            }

            var maxParameters = _database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : 50000;
            if (uniqueIds.Length > maxParameters)
            {
                var files = new List<BookFile>();
                foreach (var batch in uniqueIds.Chunk(maxParameters))
                {
                    files.AddRange(QueryForIds(batch.ToArray()));
                }

                return files.DistinctBy(f => f.Id).ToList();
            }

            return QueryForIds(uniqueIds);
        }

        private SqlBuilder BuildUnmappedFilesBuilder(string mediaType)
        {
            // No explicit Select here: Query<T>/QueryDistinct<T> add the SELECT clause
            // themselves, and a second Select(typeof(BookFile)) appends a duplicate copy
            // of every column, doubling the data returned and parsed for every row.
            var builder = new SqlBuilder(_database.DatabaseType)
                .Where<BookFile>(t => t.EditionId == 0);

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                builder.Where<BookFile>(t => t.MediaType == mediaType);
            }

            return builder;
        }

        public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, AudioProductionCorrection correction, IDbConnection connection, IDbTransaction transaction)
        {
            bool? productionOverride = correction switch
            {
                AudioProductionCorrection.Standard => false,
                AudioProductionCorrection.NonStandard => true,
                AudioProductionCorrection.Unchanged or AudioProductionCorrection.Automatic => null,
                _ => throw new ArgumentOutOfRangeException(nameof(correction))
            };
            var parameters = new { bookId, editionId, productionOverride, mediaType = mediaType.ToString().ToLowerInvariant(),
                audio = (int)BookMediaType.Audiobook, ebook = (int)BookMediaType.Ebook };
            const string mediaScope = @"COALESCE(NULLIF(""MediaType"", ''),
                (SELECT CASE ""MediaType"" WHEN @audio THEN 'audiobook' WHEN @ebook THEN 'ebook' END
                 FROM ""Books"" WHERE ""Id"" = @bookId)) = @mediaType";
            // Separate changed-column writes give exact counts, within the caller's transaction.
            var reclassified = mediaType == BookMediaType.Audiobook && correction != AudioProductionCorrection.Unchanged
                ? connection.Execute($@"UPDATE ""BookFiles"" SET ""AudioProductionOverride"" = @productionOverride
                    WHERE ""EditionId"" IN (SELECT ""Id"" FROM ""Editions"" WHERE ""BookId"" = @bookId)
                        AND {mediaScope}
                        AND ((@productionOverride IS NULL AND ""AudioProductionOverride"" IS NOT NULL) OR
                            (@productionOverride IS NOT NULL AND (""AudioProductionOverride"" IS NULL OR ""AudioProductionOverride"" <> @productionOverride)))",
                    parameters, transaction) : 0;
            var relinked = editionId.HasValue ? connection.Execute($@"UPDATE ""BookFiles"" SET ""EditionId"" = @editionId
                WHERE ""EditionId"" IN (SELECT ""Id"" FROM ""Editions"" WHERE ""BookId"" = @bookId)
                    AND {mediaScope} AND ""EditionId"" <> @editionId", parameters, transaction) : 0;
            return (relinked, reclassified);
        }

        private static string DescendantPathPredicate(string path, string folder) =>
            $"substr({path}, 1, length({folder}) + 1) = {folder} || @separator";

        private (string Predicate, DynamicParameters Parameters) GetBasePathScope(string path)
        {
            if (!OsInfo.IsWindows)
            {
                // Keep the indexable filter, then enforce literal platform case: SQLite
                // LIKE folds ASCII case, unlike Linux paths and PostgreSQL.
                var prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return (@"""Path"" LIKE @basePattern ESCAPE '!' AND " + DescendantPathPredicate("\"Path\"", "@basePath"), new DynamicParameters(new
                {
                    basePath = path.TrimEnd(Path.DirectorySeparatorChar),
                    separator = Path.DirectorySeparatorChar.ToString(),
                    basePattern = prefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%"
                }));
            }

            // Neither engine reproduces Windows ordinal Unicode path comparison. Filter
            // a thin identity projection, then fetch only matching rows, without tag JSON.
            using var connection = _database.OpenConnection();
            var ids = connection.Query<BookFile>(@"SELECT ""Id"", ""Path"" FROM ""BookFiles""")
                .Where(file => file.Path.IsNotNullOrWhiteSpace() && path.IsParentPath(file.Path))
                .Select(file => file.Id).ToArray();
            var rows = _database.DatabaseType == DatabaseType.PostgreSQL
                ? "SELECT CAST(value AS integer) FROM jsonb_array_elements_text(CAST(@pathIds AS jsonb))"
                : "SELECT value FROM json_each(@pathIds)";
            return ($"\"Id\" IN ({rows})", new DynamicParameters(new { pathIds = JsonSerializer.Serialize(ids) }));
        }

        public List<BookFile> GetFilesWithBasePath(string path)
        {
            return GetFilesWithBasePath(path, null);
        }

        public List<BookFile> GetFilesWithBasePath(string path, string mediaType)
        {
            if (string.IsNullOrWhiteSpace(path)) return new List<BookFile>();
            var scope = GetBasePathScope(path);
            var builder = new SqlBuilder(_database.DatabaseType)
                .Where(scope.Predicate, scope.Parameters);
            
            // Add MediaType filtering to prevent cross-contamination between audiobooks and ebooks
            if (!string.IsNullOrEmpty(mediaType))
            {
                builder.Where<BookFile>(x => x.MediaType == mediaType);
            }
            
            return _database.Query<BookFile>(builder).ToList();
        }

        public List<BookFile> GetFileStatsWithBasePath(string path, string mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new List<BookFile>();
            }

            // Stage filtering only needs identity, path, size, and mtime. Avoid loading tag/media-info JSON for
            // every tracked file in a root folder during rescans.
            var scope = GetBasePathScope(path);
            scope.Parameters.Add("mediaType", mediaType);
            var mediaClause = string.IsNullOrWhiteSpace(mediaType) ? string.Empty : @" AND ""MediaType"" = @mediaType";
            using var connection = _database.OpenConnection();
            return connection.Query<BookFile>(
                @"SELECT ""Id"", ""Path"", ""Size"", ""Modified"", ""EditionId"", ""MediaType"",
                         CASE WHEN substr(""MatchDetails"", 1, 13) = 'APPLY_FAILED:' THEN 'APPLY_FAILED:' ELSE NULL END AS ""MatchDetails""
                  FROM ""BookFiles""
                  WHERE " + scope.Predicate + mediaClause, scope.Parameters).ToList();
        }

        public HashSet<string> GetReplicaPathsWithBasePath(string path)
        {
            var result = new HashSet<string>(PathEqualityComparer.Instance);

            if (string.IsNullOrWhiteSpace(path))
            {
                return result;
            }

            // Only consider replicas for ebook files; replicas are stored as JSON arrays in a text column.
            try
            {
                var scope = GetBasePathScope(path);
                using var conn = _database.OpenConnection();
                var rows = conn.Query<string>(
                    @"SELECT ""ReplicaPaths""
                      FROM ""BookFiles""
                      WHERE " + scope.Predicate + @"
                        AND ""MediaType"" = 'ebook'
                        AND ""ReplicaPaths"" IS NOT NULL
                        AND ""ReplicaPaths"" != '[]';",
                    scope.Parameters).ToList();

                foreach (var json in rows)
                {
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    try
                    {
                        var paths = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                        foreach (var p in paths)
                        {
                            if (string.IsNullOrWhiteSpace(p))
                            {
                                continue;
                            }

                            result.Add(p);
                        }
                    }
                    catch
                    {
                        // Best-effort only: ignore malformed JSON for replica paths.
                    }
                }
            }
            catch
            {
                // Best-effort only: stage should still work without replica skipping.
            }

            return result;
        }

        public BookFile GetFileWithPath(string path)
        {
            return Query(x => x.Path == path).SingleOrDefault();
        }

        public List<BookFile> GetFileWithPath(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return new List<BookFile>();
            }

            var uniquePaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(PathEqualityComparer.Instance)
                .ToArray();

            if (uniquePaths.Length == 0)
            {
                return new List<BookFile>();
            }

            // Use a limited join (BookFiles + Edition) for speed; higher-level callers can load full Book/Author if needed.
            List<BookFile> QueryForPaths(string[] chunkPaths)
            {
                var builder = new SqlBuilder(_database.DatabaseType)
                    .LeftJoin<BookFile, Edition>((f, e) => f.EditionId == e.Id)
                    .Where<BookFile>(f => Enumerable.Contains(chunkPaths, f.Path));

                return _database.QueryJoined<BookFile, Edition>(builder, MapTrack).ToList();
            }

            // Avoid provider parameter limits by chunking large IN lists.
            var maxParameters = _database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : 50000;
            if (uniquePaths.Length > maxParameters)
            {
                var files = new List<BookFile>();
                foreach (var batch in uniquePaths.Chunk(maxParameters))
                {
                    files.AddRange(QueryForPaths(batch.ToArray()));
                }

                return files.DistinctBy(f => f.Id).ToList();
            }

            return QueryForPaths(uniquePaths);
        }

        public int InsertManyIgnoreDuplicatePaths(List<BookFile> files)
        {
            if (files == null || files.Count == 0)
            {
                return 0;
            }

            // Conflict-safe insert for staging/rescans where BookFiles may already contain the path.
            // Relies on a unique index/constraint on BookFiles.Path (added in migration 048).
	            var sql = _database.DatabaseType == DatabaseType.SQLite
	                ? @"INSERT OR IGNORE INTO ""BookFiles""
	                        (""Path"", ""Size"", ""Modified"", ""DateAdded"", ""OriginalFilePath"", ""SceneName"",
	                         ""ReleaseGroup"", ""Quality"", ""IndexerFlags"", ""MediaInfo"", ""EditionId"",
	                         ""CalibreId"", ""Part"", ""IsGraphicAudio"", ""AudioProductionType"", ""Narrator"",
	                         ""LastMatchAttempt"", ""MatchDetails"", ""MatchProvenance"", ""MediaType"", ""ReplicaPaths"",
	                         ""AllTags"", ""DurationSeconds"")
	                    VALUES
	                        (@Path, @Size, @Modified, @DateAdded, @OriginalFilePath, @SceneName,
	                         @ReleaseGroup, @Quality, @IndexerFlags, @MediaInfo, @EditionId,
	                         @CalibreId, @Part, @IsGraphicAudio, @AudioProductionType, @Narrator,
	                         @LastMatchAttempt, @MatchDetails, @MatchProvenance, @MediaType, @ReplicaPaths,
	                         @AllTags, @DurationSeconds);"
	                : @"INSERT INTO ""BookFiles""
	                        (""Path"", ""Size"", ""Modified"", ""DateAdded"", ""OriginalFilePath"", ""SceneName"",
	                         ""ReleaseGroup"", ""Quality"", ""IndexerFlags"", ""MediaInfo"", ""EditionId"",
	                         ""CalibreId"", ""Part"", ""IsGraphicAudio"", ""AudioProductionType"", ""Narrator"",
	                         ""LastMatchAttempt"", ""MatchDetails"", ""MatchProvenance"", ""MediaType"", ""ReplicaPaths"",
	                         ""AllTags"", ""DurationSeconds"")
	                    VALUES
	                        (@Path, @Size, @Modified, @DateAdded, @OriginalFilePath, @SceneName,
	                         @ReleaseGroup, @Quality, @IndexerFlags, @MediaInfo, @EditionId,
	                         @CalibreId, @Part, @IsGraphicAudio, @AudioProductionType, @Narrator,
	                         @LastMatchAttempt, @MatchDetails, @MatchProvenance, @MediaType, @ReplicaPaths,
	                         @AllTags, @DurationSeconds)
	                    ON CONFLICT(""Path"") DO NOTHING;";

            using var conn = _database.OpenConnection();
            using var transaction = conn.BeginTransaction();
            try
            {
                var inserted = conn.Execute(sql, files, transaction: transaction);
                transaction.Commit();
                return inserted;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // best-effort
                }
                throw;
            }
        }

        public void ReplaceMany(List<BookFile> filesToAdd, List<BookFile> filesToDelete)
        {
            filesToAdd ??= new List<BookFile>();
            filesToDelete ??= new List<BookFile>();

            var newFiles = filesToAdd.Where(file => file?.Id == 0).ToList();

            using var conn = _database.OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                ReplaceMany(filesToAdd, filesToDelete, conn, transaction);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // The original persistence exception is the useful failure.
                }

                // InsertMany assigns generated IDs before the caller commits. A rolled-back
                // replacement must remain retryable as a set of new rows.
                foreach (var file in newFiles)
                {
                    file.Id = 0;
                }

                throw;
            }
        }

        public void ReplaceMany(List<BookFile> filesToAdd, List<BookFile> filesToDelete, IDbConnection connection, IDbTransaction transaction)
        {
            filesToAdd ??= new List<BookFile>();
            filesToDelete ??= new List<BookFile>();

            if (filesToAdd.Any(file => file == null || file.Id < 0))
            {
                throw new System.InvalidOperationException("Replacement requires valid BookFile rows");
            }

            var newFiles = filesToAdd.Where(file => file.Id == 0).ToList();
            var updatedFiles = filesToAdd.Where(file => file.Id > 0).ToList();

            var deletedIds = filesToDelete
                .Where(file => file?.Id > 0)
                .Select(file => file.Id)
                .Distinct()
                .ToList();

            if (updatedFiles.Any(file => deletedIds.Contains(file.Id)))
            {
                throw new System.InvalidOperationException("A replacement cannot delete and update the same BookFile row");
            }

            DeleteMany(deletedIds, connection, transaction);
            UpdateMany(updatedFiles, connection, transaction);
            InsertMany(newFiles, connection, transaction);
        }

        private BookFile MapTrack(BookFile file, Edition book)
        {
            file.Edition = book;
            return file;
        }
    }
}
