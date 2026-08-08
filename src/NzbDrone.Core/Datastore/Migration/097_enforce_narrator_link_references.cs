using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(97)]
    public class enforce_narrator_link_references : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
                DELETE FROM ""BookNarratorLink""
                WHERE ""BookId"" NOT IN (SELECT ""Id"" FROM ""Books"")
                   OR ""NarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"");

                DELETE FROM ""EditionNarratorLink""
                WHERE ""EditionId"" NOT IN (SELECT ""Id"" FROM ""Editions"")
                   OR ""NarratorId"" NOT IN (SELECT ""Id"" FROM ""Narrators"");

                ALTER TABLE ""BookNarratorLink""
                    DROP CONSTRAINT IF EXISTS ""FK_BookNarratorLink_Books"";
                ALTER TABLE ""BookNarratorLink""
                    DROP CONSTRAINT IF EXISTS ""FK_BookNarratorLink_Narrators"";
                ALTER TABLE ""EditionNarratorLink""
                    DROP CONSTRAINT IF EXISTS ""FK_EditionNarratorLink_Editions"";
                ALTER TABLE ""EditionNarratorLink""
                    DROP CONSTRAINT IF EXISTS ""FK_EditionNarratorLink_Narrators"";

                ALTER TABLE ""BookNarratorLink""
                    ADD CONSTRAINT ""FK_BookNarratorLink_Books""
                    FOREIGN KEY (""BookId"") REFERENCES ""Books"" (""Id"") ON DELETE CASCADE;
                ALTER TABLE ""BookNarratorLink""
                    ADD CONSTRAINT ""FK_BookNarratorLink_Narrators""
                    FOREIGN KEY (""NarratorId"") REFERENCES ""Narrators"" (""Id"") ON DELETE CASCADE;
                ALTER TABLE ""EditionNarratorLink""
                    ADD CONSTRAINT ""FK_EditionNarratorLink_Editions""
                    FOREIGN KEY (""EditionId"") REFERENCES ""Editions"" (""Id"") ON DELETE CASCADE;
                ALTER TABLE ""EditionNarratorLink""
                    ADD CONSTRAINT ""FK_EditionNarratorLink_Narrators""
                    FOREIGN KEY (""NarratorId"") REFERENCES ""Narrators"" (""Id"") ON DELETE CASCADE;
            ");
        }
    }
}
