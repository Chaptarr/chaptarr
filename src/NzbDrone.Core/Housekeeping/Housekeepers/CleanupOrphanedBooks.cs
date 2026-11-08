using System.Linq;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedBooks : IHousekeepingTask
    {
        private readonly IMainDatabase _database;
        private readonly IBookService _bookService;

        public CleanupOrphanedBooks(IMainDatabase database, IBookService bookService)
        {
            _database = database;
            _bookService = bookService;
        }

        public void Clean()
        {
            using var connection = _database.OpenConnection();
            var afterId = 0;
            while (true)
            {
                // Recheck the orphan predicate per bounded batch. Rich book hydration
                // requires an Author, so pass these raw rows to normal delete hydration.
                var books = connection.Query<Book>($@"SELECT b.* FROM ""Books"" b
                    LEFT JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                    WHERE b.""Id"" > @afterId AND a.""Id"" IS NULL
                    ORDER BY b.""Id"" LIMIT {SqliteVariableLimit.MaxParameters}", new { afterId }).ToList();
                if (books.Count == 0) return;

                // Raw Book construction supplies an empty Author placeholder. The
                // anti-join proved this relationship absent; keep the numeric ID only.
                foreach (var book in books) book.LazyAuthor = null;
                _bookService.DeleteMany(books);
                afterId = books[^1].Id;
            }
        }
    }
}
