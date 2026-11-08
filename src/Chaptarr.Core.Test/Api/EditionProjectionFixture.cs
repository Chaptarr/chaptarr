using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    public class EditionProjectionFixture
    {
        [Test]
        public void facade_deduplicates_within_each_book_and_keeps_its_selected_edition()
        {
            var editions = new[]
            {
                new Edition { Id = 1, BookId = 10, GoodreadsEditionId = 123 },
                new Edition { Id = 2, BookId = 10, GoodreadsEditionId = 123, Monitored = true },
                new Edition { Id = 3, BookId = 20, GoodreadsEditionId = 123 },
                new Edition { Id = 4, BookId = 20, GoodreadsEditionId = 123 }
            };
            Assert.That(ReadarrFacadeEditionIdentity.FilterAddressableEditions(editions,
                new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"), "test"), Is.EqualTo(new[] { editions[1], editions[2] }));
            Assert.That(ReadarrFacadeEditionIdentity.FilterAddressableEditions(editions, null, "test"), Is.EqualTo(editions));
        }
    }
}
