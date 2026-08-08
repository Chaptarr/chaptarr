using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(21)]
    public class add_book_isbn_asin_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // ISBN/ASIN lookups are used for identifier-based matching and UI lookups; avoid table scans.
            Create.Index("IX_Books_ISBN10").OnTable("Books").OnColumn("ISBN10");
            Create.Index("IX_Books_ISBN13").OnTable("Books").OnColumn("ISBN13");
            Create.Index("IX_Books_ASIN").OnTable("Books").OnColumn("ASIN");
            Create.Index("IX_Books_AudibleASIN").OnTable("Books").OnColumn("AudibleASIN");
        }
    }
}

