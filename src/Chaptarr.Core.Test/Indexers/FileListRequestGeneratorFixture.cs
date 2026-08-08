using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers.FileList;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class FileListRequestGeneratorFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void should_use_default_filelist_category_for_single_media_book_searches(BookMediaType mediaType)
        {
            var generator = new FileListRequestGenerator
            {
                Settings = new FileListSettings
                {
                    Username = "user",
                    Passkey = "pass"
                }
            };

            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Ursula K Le Guin" },
                BookTitle = "A Wizard of Earthsea",
                Books = new List<Book>
                {
                    new Book { MediaType = mediaType }
                }
            };

            var requests = generator.GetSearchRequests(searchCriteria)
                                    .GetTier(0)
                                    .SelectMany(page => page)
                                    .ToList();

            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests.Select(r => r.HttpRequest.Url.FullUri), Has.All.Contain("category=16"));
        }
    }
}
