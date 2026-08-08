using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(99)]
    public class add_custom_format_media_type : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("CustomFormats").Column("AppliesTo").Exists())
            {
                Alter.Table("CustomFormats")
                    .AddColumn("AppliesTo")
                    .AsInt32()
                    .NotNullable()
                    .WithDefaultValue(0);
            }

            foreach (var builtInKey in new[]
                     {
                         "dramatized-full-cast-audio",
                         "standard-non-dramatized-audio",
                         "preferred-narrator",
                         "preferred-narrator-majority",
                         "complete-preferred-cast"
                     })
            {
                Update.Table("CustomFormats")
                    .Set(new { AppliesTo = 1 })
                    .Where(new { BuiltInKey = builtInKey });
            }
        }
    }
}
