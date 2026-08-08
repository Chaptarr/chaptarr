using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseSearchServiceQualityProfileFixture
    {
        [Test]
        public void should_report_audiobook_profile_availability_by_media_type()
        {
            var author = new Author
            {
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = null
            };

            Assert.That(ReleaseSearchService.HasConfiguredQualityProfileForMediaType(author, BookMediaType.Audiobook), Is.True);
            Assert.That(ReleaseSearchService.HasConfiguredQualityProfileForMediaType(author, BookMediaType.Ebook), Is.False);
        }

        [Test]
        public void should_filter_books_without_matching_media_profile()
        {
            var author = new Author
            {
                AudiobookQualityProfileId = null,
                EbookQualityProfileId = 3
            };

            var audiobook = new Book { Id = 1, Title = "Audio", MediaType = BookMediaType.Audiobook };
            var ebook = new Book { Id = 2, Title = "Ebook", MediaType = BookMediaType.Ebook };

            var filtered = ReleaseSearchService.FilterBooksByConfiguredMediaProfiles(author, new List<Book> { audiobook, ebook });

            Assert.That(filtered, Has.Count.EqualTo(1));
            Assert.That(filtered[0].Id, Is.EqualTo(ebook.Id));
        }
    }
}
