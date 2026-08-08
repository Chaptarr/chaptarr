using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(54)]
    public class add_books_unit_key_hash : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Books").Column("UnitKeyHash").Exists())
            {
                Alter.Table("Books")
                    .AddColumn("UnitKeyHash").AsString().Nullable();
            }

            if (!Schema.Table("Books").Index("IX_Books_BaseBookId_MediaType_UnitKeyHash").Exists())
            {
                Create.Index("IX_Books_BaseBookId_MediaType_UnitKeyHash")
                    .OnTable("Books")
                    .OnColumn("BaseBookId").Ascending()
                    .OnColumn("MediaType").Ascending()
                    .OnColumn("UnitKeyHash").Ascending();
            }
        }
    }
}

