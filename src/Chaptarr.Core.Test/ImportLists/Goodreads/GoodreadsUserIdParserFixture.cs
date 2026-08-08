using NUnit.Framework;
using NzbDrone.Core.ImportLists.Goodreads;

namespace Chaptarr.Core.Test.ImportLists.Goodreads
{
    [TestFixture]
    public class GoodreadsUserIdParserFixture
    {
        [TestCase("12345678", true, "12345678")]
        [TestCase("12345678-example-user", true, "12345678")]
        [TestCase("https://www.goodreads.com/user/show/12345678-example-user", true, "12345678")]
        [TestCase("https://www.goodreads.com/user/show/12345678", true, "12345678")]
        [TestCase("42", true, "42")]
        [TestCase("  12345678  ", true, "12345678")]
        [TestCase("", false, null)]
        [TestCase(null, false, null)]
        [TestCase("   ", false, null)]
        [TestCase("not-a-user", false, null)]
        [TestCase("ab", false, null)]
        public void should_parse_user_id_from_common_inputs(string input, bool isValid, string expected)
        {
            var parsed = GoodreadsUserIdParser.TryParse(input, out var userId);

            Assert.That(parsed, Is.EqualTo(isValid));
            Assert.That(userId, Is.EqualTo(expected));
            Assert.That(GoodreadsUserIdParser.IsValidUserId(input), Is.EqualTo(isValid));
        }
    }
}
