using System;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class V5AuthorDateFixture
    {
        private static readonly DateTime Today = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

        [Test]
        public void should_parse_canonical_non_future_author_date()
        {
            Assert.That("1963-11-22".ToValidAuthorDate(Today), Is.EqualTo(new DateTime(1963, 11, 22)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("2026-02-30")]
        [TestCase("2026/08/25")]
        [TestCase("2027-01-01")]
        [TestCase("1963-11-22\0junk")]
        public void should_reject_missing_malformed_or_future_author_date(string value)
        {
            Assert.That(value.ToValidAuthorDate(Today), Is.Null);
        }
    }
}
