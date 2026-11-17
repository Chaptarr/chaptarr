using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookTagServiceIsbnTagFixture
    {
        [TestCase("not an isbn")]
        [TestCase("1234567890")]
        [TestCase("   ")]
        [TestCase("")]
        [TestCase(null)]
        public void should_not_store_an_isbn_tag_when_the_isbn_cannot_be_parsed(string raw)
        {
            var tags = new Dictionary<string, string>();

            EBookTagService.AddIsbnTag(tags, raw);

            Assert.That(tags.ContainsKey("isbn"), Is.False);
            Assert.That(tags.Values.Any(v => v == null), Is.False);
        }

        [TestCase("978-0-306-40615-7", "9780306406157")]
        [TestCase("0-306-40615-2", "0306406152")]
        public void should_store_the_stripped_isbn_when_it_is_valid(string raw, string expected)
        {
            var tags = new Dictionary<string, string>();

            EBookTagService.AddIsbnTag(tags, raw);

            Assert.That(tags["isbn"], Is.EqualTo(expected));
        }
    }
}
