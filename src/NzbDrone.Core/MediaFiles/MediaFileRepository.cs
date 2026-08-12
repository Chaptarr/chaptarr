using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dapper;
using NzbDrone.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileRepository : IBasicRepository<BookFile>
    {
        List<BookFile> GetFilesByAuthor(int authorId);
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
        void DeleteFilesByBook(int bookId);
        void UnlinkFilesByBook(int bookId);
        void UnlinkFilesByEdition(int editionId);
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
            return Query(Builder().Where<Book>(b => b.AuthorId == authorId));
        }

        public List<BookFile> GetFilesByBook(int bookId)
        {
            return Query(Builder().Where<Book>(b => b.Id == bookId));
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
            // Do not call .Select(typeof(BookFile)) here - SqlMapperExtensions.Query<T>(builder)
            // already adds the SELECT clause for T, so pre-selecting it here duplicated every
            // column in the projection (SELECT "BookFiles".*, "BookFiles".*), which made Dapper's
            // generated row deserializer read misaligned columns and throw (surfacing as a
            // System.Data.DataException / OutOfMemoryException on GET /api/v1/bookFile?unmapped=true).
            var builder = new SqlBuilder(_database.DatabaseType)
                .Where<BookFile>(t => t.EditionId == 0);

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                builder.Where<BookFile>(t => t.MediaType == mediaType);
            }

            return builder;
        }

        public void DeleteFilesByBook(int bookId)
        {
            var fileIds = GetFilesByBook(bookId).Select(x => x.Id).ToList();
            Delete(x => fileIds.Contains(x.Id));
        }

        public void UnlinkFilesByBook(int bookId)
        {
            var files = GetFilesByBook(bookId);
            files.ForEach(x => x.EditionId = 0);
            SetFields(files, f => f.EditionId);
        }

        public void UnlinkFilesByEdition(int editionId)
        {
            var files = GetFilesByEdition(editionId);
            files.ForEach(x => x.EditionId = 0);
            SetFields(files, f => f.EditionId);
        }

        public List<BookFile> GetFilesWithBasePath(string path)
        {
            return GetFilesWithBasePath(path, null);
        }

        public List<BookFile> GetFilesWithBasePath(string path, string mediaType)
        {
            // ensure path ends with a single trailing path separator to avoid matching partial paths
            var safePath = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var builder = new SqlBuilder(_database.DatabaseType).Where<BookFile>(x => x.Path.StartsWith(safePath));
            
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
            var safePath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var likePath = safePath + "%";

            using var conn = _database.OpenConnection();
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return conn.Query<BookFile>(
                    @"SELECT ""Id"", ""Path"", ""Size"", ""Modified"", ""EditionId"", ""MediaType""
                      FROM ""BookFiles""
                      WHERE ""Path"" LIKE @likePath;",
                    new { likePath }).ToList();
            }

            return conn.Query<BookFile>(
                @"SELECT ""Id"", ""Path"", ""Size"", ""Modified"", ""EditionId"", ""MediaType""
                  FROM ""BookFiles""
                  WHERE ""Path"" LIKE @likePath
                    AND ""MediaType"" = @mediaType;",
                new { likePath, mediaType }).ToList();
        }

        public HashSet<string> GetReplicaPathsWithBasePath(string path)
        {
            var result = new HashSet<string>(PathEqualityComparer.Instance);

            if (string.IsNullOrWhiteSpace(path))
            {
                return result;
            }

            // Only consider replicas for ebook files; replicas are stored as JSON arrays in a text column.
            var safePath = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var likePath = safePath + "%";

            try
            {
                using var conn = _database.OpenConnection();
                var rows = conn.Query<string>(
                    @"SELECT ""ReplicaPaths""
                      FROM ""BookFiles""
                      WHERE ""Path"" LIKE @likePath
                        AND ""MediaType"" = 'ebook'
                        AND ""ReplicaPaths"" IS NOT NULL
                        AND ""ReplicaPaths"" != '[]';",
                    new { likePath }).ToList();

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

            if (filesToAdd.Any(file => file == null || file.Id != 0))
            {
                throw new System.InvalidOperationException("Replacement inserts require new BookFile rows with Id 0");
            }

            var deletedIds = filesToDelete
                .Where(file => file?.Id > 0)
                .Select(file => file.Id)
                .Distinct()
                .ToList();

            using var conn = _database.OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                DeleteMany(deletedIds, conn, transaction);
                InsertMany(filesToAdd, conn, transaction);
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
                foreach (var file in filesToAdd)
                {
                    file.Id = 0;
                }

                throw;
            }
        }

        private BookFile MapTrack(BookFile file, Edition book)
        {
            file.Edition = book;
            return file;
        }
    }
}
