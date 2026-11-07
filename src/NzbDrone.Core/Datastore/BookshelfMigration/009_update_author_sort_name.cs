// Source: BookshelfNG main-v0.4.21.71 (3fad9427d167ed69d30ca6ed55f40262c2846487), GPL-3.0.
// This isolated copy upgrades a disposable BookshelfNG source snapshot before import.
using System.Data;
using Dapper;
using FluentMigrator;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.BookshelfMigration
{
    [Migration(009)]
    public class update_author_sort_name : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("AuthorMetadata").AddColumn("SortName").AsString().Nullable();
            Execute.WithConnection(MigrateAuthorSortName);
            Alter.Table("AuthorMetadata").AlterColumn("SortName").AsString().NotNullable();

            Delete.Column("SortName").FromTable("Authors");
        }

        private void MigrateAuthorSortName(IDbConnection conn, IDbTransaction tran)
        {
            var rows = conn.Query<AuthorName>("SELECT \"AuthorMetadata\".\"Id\", \"AuthorMetadata\".\"Name\" FROM \"AuthorMetadata\"", transaction: tran);

            foreach (var row in rows)
            {
                row.SortName = row.Name.ToLastFirst().ToLower();
            }

            var sql = "UPDATE \"AuthorMetadata\" SET \"SortName\" = @SortName WHERE \"Id\" = @Id";
            conn.Execute(sql, rows, transaction: tran);
        }

        private class AuthorName : ModelBase
        {
            public string Name { get; set; }
            public string SortName { get; set; }
        }
    }
}
