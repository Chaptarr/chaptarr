using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(70)]
    public class add_edition_chapters_and_review_count : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Editions").Exists())
            {
                return;
            }

            if (!Schema.Table("Editions").Column("ReviewCount").Exists())
            {
                Alter.Table("Editions")
                    .AddColumn("ReviewCount").AsInt32().Nullable();
            }

            if (!Schema.Table("Editions").Column("Chapters").Exists())
            {
                Alter.Table("Editions")
                    .AddColumn("Chapters").AsString().WithDefaultValue("[]");
            }

            IfDatabase("sqlite").Execute.Sql(@"
                UPDATE ""Editions""
                   SET ""Chapters"" = '[]'
                 WHERE ""Chapters"" IS NULL
                    OR TRIM(""Chapters"") = ''
                    OR ""Chapters"" = 'null'
                    OR ""Chapters"" = '{}';
            ");

            IfPostgres().Execute.Sql(@"
                UPDATE ""Editions""
                   SET ""Chapters"" = '[]'
                 WHERE ""Chapters"" IS NULL
                    OR btrim(""Chapters"") = ''
                    OR ""Chapters"" = 'null'
                    OR ""Chapters"" = '{}';
            ");
        }
    }
}
