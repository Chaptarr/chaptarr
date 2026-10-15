using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IFuzzyMatchingRepository
    {
        void PopulateFts5Tables();
    }

    public class FuzzyMatchingRepository : IFuzzyMatchingRepository
    {
        private readonly IMainDatabase _database;
        private readonly DatabaseType _dbType;

        public FuzzyMatchingRepository(IMainDatabase database)
        {
            _database = database;
            _dbType = database.DatabaseType;
        }

        public void PopulateFts5Tables()
        {
            if (_dbType != DatabaseType.SQLite)
            {
                return;
            }

            using (var connection = _database.OpenConnection())
            {
                try
                {
                    // Populate book_fts table
                    connection.Execute(@"
                        INSERT INTO book_fts (BookId, Title)
                        SELECT b.Id, b.Title
                        FROM ""Books"" b
                        LEFT JOIN book_fts bf ON b.Id = bf.BookId
                        WHERE bf.BookId IS NULL");

                    // Populate author_fts table
                    connection.Execute(@"
                        INSERT INTO author_name_fts (AuthorId, Name)
                        SELECT a.Id, a.Name
                        FROM ""Authors"" a
                        LEFT JOIN author_name_fts af ON a.Id = af.AuthorId
                        WHERE af.AuthorId IS NULL");
                }
                catch
                {
                    // FTS5 might not be available
                }
            }
        }
    }
}
