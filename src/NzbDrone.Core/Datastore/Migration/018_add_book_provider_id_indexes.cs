using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(18)]
    public class add_book_provider_id_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Provider ID lookups are performance-critical; index the columns that are frequently queried.
            Create.Index("IX_Books_GoodreadsBookId").OnTable("Books").OnColumn("GoodreadsBookId");
            Create.Index("IX_Books_GoodreadsWorkId").OnTable("Books").OnColumn("GoodreadsWorkId");
            Create.Index("IX_Books_OpenLibraryEditionId").OnTable("Books").OnColumn("OpenLibraryEditionId");
            Create.Index("IX_Books_OpenLibraryWorkId").OnTable("Books").OnColumn("OpenLibraryWorkId");
            Create.Index("IX_Books_GoogleBooksId").OnTable("Books").OnColumn("GoogleBooksId");
            Create.Index("IX_Books_LibraryThingId").OnTable("Books").OnColumn("LibraryThingId");
        }
    }
}

