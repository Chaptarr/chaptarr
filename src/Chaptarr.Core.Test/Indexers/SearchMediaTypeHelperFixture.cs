using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class SearchMediaTypeHelperFixture
    {
        [Test]
        public void should_only_use_audio_categories_for_audiobook_searches()
        {
            var categories = new[] { 2000, 3000, 3010, 3030, 3040, 3050, 5000, 7020, 8010, 3030 };

            var filtered = SearchMediaTypeHelper.FilterCategoriesForMediaType(categories, BookMediaType.Audiobook);

            Assert.That(filtered, Is.EqualTo(new List<int> { 3000, 3010, 3030, 3040, 3050 }));
        }

        [Test]
        public void should_only_use_book_categories_for_ebook_searches()
        {
            var categories = new[] { 2000, 3000, 3030, 5000, 7000, 7010, 7020, 7030, 7040, 8010, 7020 };

            var filtered = SearchMediaTypeHelper.FilterCategoriesForMediaType(categories, BookMediaType.Ebook);

            Assert.That(filtered, Is.EqualTo(new List<int> { 7000, 7010, 7020, 7030, 7040 }));
        }

        [Test]
        public void should_keep_all_configured_categories_for_mixed_media_searches()
        {
            var categories = new[] { 2000, 3030, 7020, 7020 };

            var filtered = SearchMediaTypeHelper.FilterCategoriesForMediaType(categories, null);

            Assert.That(filtered, Is.EqualTo(new List<int> { 2000, 3030, 7020 }));
        }
    }
}
