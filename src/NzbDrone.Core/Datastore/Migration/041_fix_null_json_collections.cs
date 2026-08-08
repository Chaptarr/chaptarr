using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(41)]
    public class fix_null_json_collections : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                // These fields are stored as JSON in text columns. Some legacy/buggy rows can contain:
                // - NULL (DB null)
                // - "" (empty string)
                // - "null" (stringified JSON null)
                // - "{}" (object where an array is expected)
                //
                // Any of the above can cause mapper crashes (e.g., null collections + JsonClone/ConcatToString).
                // Normalize them to empty arrays for cross-DB compatibility (SQLite/Postgres).

                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""Images"" = '[]'
                      WHERE ""Images"" IS NULL OR ""Images"" = '' OR ""Images"" = 'null' OR ""Images"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""Links"" = '[]'
                      WHERE ""Links"" IS NULL OR ""Links"" = '' OR ""Links"" = 'null' OR ""Links"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""Genres"" = '[]'
                      WHERE ""Genres"" IS NULL OR ""Genres"" = '' OR ""Genres"" = 'null' OR ""Genres"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Books""
                      SET ""Images"" = '[]'
                      WHERE ""Images"" IS NULL OR ""Images"" = '' OR ""Images"" = 'null' OR ""Images"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Books""
                      SET ""Links"" = '[]'
                      WHERE ""Links"" IS NULL OR ""Links"" = '' OR ""Links"" = 'null' OR ""Links"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Books""
                      SET ""Genres"" = '[]'
                      WHERE ""Genres"" IS NULL OR ""Genres"" = '' OR ""Genres"" = 'null' OR ""Genres"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Books""
                      SET ""RelatedBooks"" = '[]'
                      WHERE ""RelatedBooks"" IS NULL OR ""RelatedBooks"" = '' OR ""RelatedBooks"" = 'null' OR ""RelatedBooks"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Editions""
                      SET ""Images"" = '[]'
                      WHERE ""Images"" IS NULL OR ""Images"" = '' OR ""Images"" = 'null' OR ""Images"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Editions""
                      SET ""Links"" = '[]'
                      WHERE ""Links"" IS NULL OR ""Links"" = '' OR ""Links"" = 'null' OR ""Links"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Editions""
                      SET ""NarratorNames"" = '[]'
                      WHERE ""NarratorNames"" IS NULL OR ""NarratorNames"" = '' OR ""NarratorNames"" = 'null' OR ""NarratorNames"" = '{}';",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""Editions""
                      SET ""Asins"" = '[]'
                      WHERE ""Asins"" IS NULL OR ""Asins"" = '' OR ""Asins"" = 'null' OR ""Asins"" = '{}';",
                    transaction: transaction);
            });
        }
    }
}
