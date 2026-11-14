using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Files
{
    public interface IExtraFileRepository<TExtraFile> : IBasicRepository<TExtraFile>
        where TExtraFile : ExtraFile, new()
    {
        void DeleteForAuthor(int authorId);
        void DeleteForBook(int authorId, int bookId);
        void DeleteForBookFile(int bookFileId);
        List<TExtraFile> GetFilesByAuthor(int authorId);
        List<TExtraFile> GetFilesForAuthorDeletion(IReadOnlyCollection<int> authorIds);
        List<TExtraFile> GetFilesByBook(int authorId, int bookId);
        List<TExtraFile> GetFilesByBookFile(int bookFileId);
        List<TExtraFile> GetFilesByBookFiles(IEnumerable<int> bookFileIds);
        List<TExtraFile> GetFilesForDeletion(IReadOnlyCollection<BookFile> files);
        TExtraFile FindByPath(int authorId, string path);
    }

    public class ExtraFileRepository<TExtraFile> : BasicRepository<TExtraFile>, IExtraFileRepository<TExtraFile>
        where TExtraFile : ExtraFile, new()
    {
        public ExtraFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public void DeleteForAuthor(int authorId)
        {
            Delete(c => c.AuthorId == authorId);
        }

        public void DeleteForBook(int authorId, int bookId)
        {
            Delete(c => c.AuthorId == authorId && c.BookId == bookId);
        }

        public void DeleteForBookFile(int bookFileId)
        {
            Delete(c => c.BookFileId == bookFileId);
        }

        public List<TExtraFile> GetFilesByAuthor(int authorId)
        {
            return Query(c => c.AuthorId == authorId);
        }

        public List<TExtraFile> GetFilesForAuthorDeletion(IReadOnlyCollection<int> authorIds)
        {
            // One scan per table, including escaped paths owned by unrelated authors.
            // JSON parameters preserve the original operation boundary above SQLite's variable limit.
            var ids = _database.DatabaseType == DatabaseType.PostgreSQL
                ? "SELECT CAST(value AS integer) FROM jsonb_array_elements_text(CAST(@ids AS jsonb))"
                : "SELECT value FROM json_each(@ids)";
            using var connection = _database.OpenConnection();
            return connection.Query<TExtraFile>($@"SELECT * FROM ""{_table}""
                WHERE ""AuthorId"" IN ({ids}) OR ""RelativePath"" LIKE '%..%'",
                new { ids = System.Text.Json.JsonSerializer.Serialize(authorIds.Distinct()) }).ToList();
        }

        public List<TExtraFile> GetFilesByBook(int authorId, int bookId)
        {
            return Query(c => c.AuthorId == authorId && c.BookId == bookId);
        }

        public List<TExtraFile> GetFilesByBookFile(int bookFileId)
        {
            return Query(c => c.BookFileId == bookFileId);
        }

        public List<TExtraFile> GetFilesByBookFiles(IEnumerable<int> bookFileIds)
        {
            return bookFileIds.Distinct().Select(id => (int?)id).Chunk(SqliteVariableLimit.MaxParameters)
                .SelectMany(ids => Query(c => ids.Contains(c.BookFileId))).ToList();
        }

        public List<TExtraFile> GetFilesForDeletion(IReadOnlyCollection<BookFile> files)
        {
            if (files.Count == 0) return new List<TExtraFile>();
            var selectedIds = files.Select(file => file.Id).ToHashSet();
            var extras = GetFilesByBookFiles(selectedIds);
            var bookIds = files.Where(file => file.Edition?.BookId > 0).Select(file => file.Edition.BookId).Distinct();
            using var connection = _database.OpenConnection();
            var bookPredicate = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@BookIds)" : "IN @BookIds";
            foreach (var batch in bookIds.Chunk(SqliteVariableLimit.MaxParameters))
            {
                // Preview runs before deletion; events can run after the file rows are removed.
                // Either way, only an unselected surviving part keeps the shared book extras.
                var survivors = connection.Query<(int FileId, int BookId)>(
                    "SELECT f.\"Id\", e.\"BookId\" FROM \"BookFiles\" f JOIN \"Editions\" e ON e.\"Id\" = f.\"EditionId\" WHERE e.\"BookId\" " + bookPredicate,
                    new { BookIds = batch }).Where(row => !selectedIds.Contains(row.FileId)).Select(row => row.BookId).ToHashSet();
                var removedBooks = batch.Where(id => !survivors.Contains(id)).Select(id => (int?)id).ToArray();
                if (removedBooks.Length > 0)
                    extras.AddRange(Query(extra => extra.BookFileId == null && removedBooks.Contains(extra.BookId)));
            }

            return extras.DistinctBy(extra => extra.Id).ToList();
        }

        public TExtraFile FindByPath(int authorId, string path)
        {
            return Query(c => c.AuthorId == authorId && c.RelativePath == path).SingleOrDefault();
        }
    }
}
