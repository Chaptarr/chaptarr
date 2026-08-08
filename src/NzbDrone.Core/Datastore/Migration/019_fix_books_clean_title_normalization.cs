using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(19)]
    public class fix_books_clean_title_normalization : NzbDroneMigrationBase
    {
        private sealed class BookRow
        {
            public int Id { get; set; }
            public string Title { get; set; }
        }

        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var books = connection.Query<BookRow>("SELECT \"Id\", \"Title\" FROM \"Books\";", transaction: transaction).ToList();

                if (books.Count == 0)
                {
                    return;
                }

                var updates = new List<object>(books.Count);

                foreach (var book in books)
                {
                    updates.Add(new
                    {
                        book.Id,
                        CleanTitle = (book.Title ?? string.Empty).CleanBookTitle().CleanAuthorName()
                    });
                }

                connection.Execute("UPDATE \"Books\" SET \"CleanTitle\" = @CleanTitle WHERE \"Id\" = @Id;", updates, transaction);
            });
        }
    }
}
