using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class TableMapperSortKeyValidationFixture
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_reject_unknown_table_alias()
        {
            Assert.That(TableMapping.Mapper.IsValidSortKey("authorMetadata.SortName"), Is.False);
        }

        [Test]
        public void should_reject_column_not_mapped_to_table()
        {
            Assert.That(TableMapping.Mapper.IsValidSortKey("Authors.ReleaseDate"), Is.False);
        }

        [Test]
        public void should_accept_valid_author_sort_key()
        {
            Assert.That(TableMapping.Mapper.IsValidSortKey("Authors.SortName"), Is.True);
        }
    }
}
