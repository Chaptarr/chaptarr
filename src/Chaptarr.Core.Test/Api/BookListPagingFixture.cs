using System.Linq;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookListPagingFixture
    {
        private static readonly int[] Ids = { 5, 3, 1, 4, 2 };

        [Test]
        public void should_return_the_full_list_without_offset_or_limit()
        {
            var items = Ids.ToList();

            Assert.That(BookListPaging.Slice(items, null, null, x => x), Is.EqualTo(new[] { 5, 3, 1, 4, 2 }));
        }

        [Test]
        public void should_slice_by_id_order()
        {
            Assert.That(BookListPaging.Slice(Ids.ToList(), 1, 2, x => x), Is.EqualTo(new[] { 2, 3 }));
        }

        [Test]
        public void should_return_empty_beyond_the_end()
        {
            Assert.That(BookListPaging.Slice(Ids.ToList(), 10, 5, x => x), Is.Empty);
        }

        [Test]
        public void should_clamp_limit_and_negative_offset()
        {
            var many = Enumerable.Range(1, 1500).ToList();

            Assert.That(BookListPaging.Slice(many, -4, 5000, x => x), Has.Count.EqualTo(BookListPaging.MaxLimit));
            Assert.That(BookListPaging.Slice(many, 0, 0, x => x), Has.Count.EqualTo(1));
        }

        [Test]
        public void should_cap_window_when_only_offset_is_given()
        {
            var many = Enumerable.Range(1, 1500).ToList();

            Assert.That(BookListPaging.Slice(many, 1200, null, x => x), Has.Count.EqualTo(300));
        }
    }
}
