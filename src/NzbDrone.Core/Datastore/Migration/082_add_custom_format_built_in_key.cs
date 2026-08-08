using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(82)]
    public class add_custom_format_built_in_key : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("CustomFormats").Exists() ||
                Schema.Table("CustomFormats").Column("BuiltInKey").Exists())
            {
                return;
            }

            Alter.Table("CustomFormats")
                .AddColumn("BuiltInKey")
                .AsString()
                .Nullable();
        }
    }
}
