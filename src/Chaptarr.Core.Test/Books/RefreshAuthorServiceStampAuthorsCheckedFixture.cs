using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceStampAuthorsCheckedFixture
    {
        [Test]
        public void should_use_postgres_array_predicate_when_stamping_checked_authors()
        {
            var sql = RefreshAuthorService.BuildStampAuthorsCheckedSql(DatabaseType.PostgreSQL);

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain(@"= ANY(@Ids)"));
                Assert.That(sql, Does.Not.Contain(@"IN @Ids"));
            });
        }

        [Test]
        public void should_use_sqlite_in_predicate_when_stamping_checked_authors()
        {
            var sql = RefreshAuthorService.BuildStampAuthorsCheckedSql(DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain(@"IN @Ids"));
                Assert.That(sql, Does.Not.Contain(@"= ANY(@Ids)"));
            });
        }
    }
}
