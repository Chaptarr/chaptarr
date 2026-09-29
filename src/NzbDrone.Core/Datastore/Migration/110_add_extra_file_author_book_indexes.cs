using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // ExtraFileService<T> looks its rows up (GetFilesByBook) and deletes them (DeleteForBook, run once per
    // deleted book by every BookDeletedEvent) with WHERE "AuthorId" = @a AND "BookId" = @b. Neither table
    // had an index on those columns, so each lookup/delete was a sequential scan: ~5 ms against
    // MetadataFiles at ~70k rows, which made it the single most expensive statement of an author refresh
    // that prunes books (a book deletion costs ~0.1 ms everywhere else on the delete path).
    [Migration(110)]
    public class add_extra_file_author_book_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            AddIndex("MetadataFiles", "IX_MetadataFiles_AuthorId_BookId");
            AddIndex("ExtraFiles", "IX_ExtraFiles_AuthorId_BookId");
        }

        private void AddIndex(string tableName, string indexName)
        {
            if (!Schema.Table(tableName).Exists() ||
                !Schema.Table(tableName).Column("AuthorId").Exists() ||
                !Schema.Table(tableName).Column("BookId").Exists() ||
                Schema.Table(tableName).Index(indexName).Exists())
            {
                return;
            }

            Create.Index(indexName)
                .OnTable(tableName)
                .OnColumn("AuthorId").Ascending()
                .OnColumn("BookId").Ascending();
        }
    }
}
