using Chaptarr.Api.V1.ImportLists;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ImportListBookshelfCompatibilityFixture
    {
        [Test]
        public void resource_should_expose_legacy_list_order_alias()
        {
            var resource = new ImportListResource { ListType = ImportListType.Hardcover };

            Assert.That(resource.ListOrder, Is.EqualTo(2));
        }
    }
}
