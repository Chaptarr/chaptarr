using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class PerFileBookMemoFixture
    {
        private readonly List<List<int>> _loads = new List<List<int>>();

        [SetUp]
        public void Setup() => _loads.Clear();

        private List<Book> LoadMany(List<int> ids)
        {
            _loads.Add(ids.ToList());
            return ids.Where(i => i != 99).Select(i => new Book { Id = i }).ToList();
        }

        [Test]
        public void should_load_each_book_id_only_once_across_files()
        {
            var memo = new FileMatchingService.PerFileFtsMemo();

            var first = memo.GetBooks(new[] { 1, 2, 3 }, LoadMany);
            var second = memo.GetBooks(new[] { 2, 3, 4 }, LoadMany);

            Assert.That(first.Select(b => b.Id), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(second.Select(b => b.Id), Is.EquivalentTo(new[] { 2, 3, 4 }));
            Assert.That(_loads, Has.Count.EqualTo(2));
            Assert.That(_loads[1], Is.EqualTo(new[] { 4 }));
        }

        [Test]
        public void should_not_call_the_loader_when_everything_is_cached()
        {
            var memo = new FileMatchingService.PerFileFtsMemo();
            memo.GetBooks(new[] { 1, 2 }, LoadMany);

            memo.GetBooks(new[] { 2, 1 }, LoadMany);

            Assert.That(_loads, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_return_the_same_instance_for_repeated_single_lookups()
        {
            var memo = new FileMatchingService.PerFileFtsMemo();
            var calls = 0;

            Book Load(int id)
            {
                calls++;
                return new Book { Id = id };
            }

            var a = memo.GetBook(7, Load);
            var b = memo.GetBook(7, Load);

            Assert.That(b, Is.SameAs(a));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void should_not_cache_missing_books()
        {
            var memo = new FileMatchingService.PerFileFtsMemo();
            var calls = 0;

            Book Load(int id)
            {
                calls++;
                return null;
            }

            Assert.That(memo.GetBook(5, Load), Is.Null);
            Assert.That(memo.GetBook(5, Load), Is.Null);
            Assert.That(calls, Is.EqualTo(2));

            memo.GetBooks(new[] { 99 }, LoadMany);
            memo.GetBooks(new[] { 99 }, LoadMany);
            Assert.That(_loads, Has.Count.EqualTo(2));
        }
    }
}
