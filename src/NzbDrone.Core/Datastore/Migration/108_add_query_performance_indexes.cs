using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Indexes for the queries that read the most data on a large library, picked by
    // profiling a real library's database:
    //
    // - IX_BookFiles_EditionId_Size: the per-book file size and count roll-ups (author
    //   progress, book index summary) read only EditionId and Size. BookFiles rows carry
    //   large JSON columns, so without this index those roll-ups read the whole table.
    //   It also serves every EditionId lookup, which replaces IX_BookFiles_EditionId.
    // - IX_Books_AuthorId_ReleaseDate_Id: the author index's next/last book queries rank
    //   each author's books by release date. This lets them rank from the index alone.
    // - IX_Books_MediaType_TitleSort: the book index pages sort by the lower-cased clean
    //   title. With the same expression indexed, a page is read in order instead of
    //   sorting every book of that media type first. CleanTitle and Title ride along so
    //   the A-Z jump bar counts can be read from the index alone; without them that
    //   query walks the index and fetches every book row out of order, which is slower
    //   than the sort it replaces.
    // - IX_Editions_Monitored_BookId: Wanted (missing and cutoff unmet) starts from the
    //   monitored editions and needs only their BookId. This replaces IX_Editions_Monitored.
    [Migration(108)]
    public class add_query_performance_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("BookFiles").Exists())
            {
                if (!Schema.Table("BookFiles").Index("IX_BookFiles_EditionId_Size").Exists())
                {
                    Create.Index("IX_BookFiles_EditionId_Size")
                        .OnTable("BookFiles")
                        .OnColumn("EditionId").Ascending()
                        .OnColumn("Size").Ascending();
                }

                if (Schema.Table("BookFiles").Index("IX_BookFiles_EditionId").Exists())
                {
                    Delete.Index("IX_BookFiles_EditionId").OnTable("BookFiles");
                }
            }

            if (Schema.Table("Books").Exists())
            {
                if (!Schema.Table("Books").Index("IX_Books_AuthorId_ReleaseDate_Id").Exists())
                {
                    Create.Index("IX_Books_AuthorId_ReleaseDate_Id")
                        .OnTable("Books")
                        .OnColumn("AuthorId").Ascending()
                        .OnColumn("ReleaseDate").Ascending()
                        .OnColumn("Id").Ascending();
                }

                // An index on an expression is only used when the query spells the expression
                // the same way, so this must stay in step with the title sort in BookRepository.
                Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Books_MediaType_TitleSort""
                    ON ""Books"" (""MediaType"", (LOWER(COALESCE(""CleanTitle"", ""Title"", ''))), ""Id"", ""CleanTitle"", ""Title"");");
            }

            if (Schema.Table("Editions").Exists())
            {
                if (!Schema.Table("Editions").Index("IX_Editions_Monitored_BookId").Exists())
                {
                    Create.Index("IX_Editions_Monitored_BookId")
                        .OnTable("Editions")
                        .OnColumn("Monitored").Ascending()
                        .OnColumn("BookId").Ascending();
                }

                if (Schema.Table("Editions").Index("IX_Editions_Monitored").Exists())
                {
                    Delete.Index("IX_Editions_Monitored").OnTable("Editions");
                }
            }
        }
    }
}
