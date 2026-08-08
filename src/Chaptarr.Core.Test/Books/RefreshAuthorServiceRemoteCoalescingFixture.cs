using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceRemoteCoalescingFixture
    {
        [Test]
        public void should_keep_remote_books_that_share_stable_work_ids_when_pockets_are_not_identical()
        {
            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "First",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-one" }
                }
            };

            var duplicate = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Duplicate",
                HardcoverBookId = "hc:2",
                GoodreadsWorkId = "gr:1", // overlaps by provider ID intersection
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-two" }
                }
            };

            var distinct = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Distinct",
                HardcoverBookId = "hc:3",
                GoodreadsWorkId = "gr:3",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-three" }
                }
            };

            var input = new List<Book> { first, duplicate, distinct };

            var result = RefreshAuthorService.CoalesceIdenticalRemoteBookPockets(input);

            Assert.Multiple(() =>
            {
                Assert.That(result.Count, Is.EqualTo(3));
                Assert.That(result[0], Is.SameAs(first));
                Assert.That(result[1], Is.SameAs(duplicate));
                Assert.That(result[2], Is.SameAs(distinct));
            });
        }

        [Test]
        public void should_not_collapse_remote_books_that_only_share_edition_aliases()
        {
            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "First",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-one", Asin = "B000SHARED" }
                }
            };

            var distinct = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Distinct",
                HardcoverBookId = "hc:2",
                GoodreadsWorkId = "gr:2",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-two", Asin = "B000SHARED" }
                }
            };

            var input = new List<Book> { first, distinct };

            var result = RefreshAuthorService.CoalesceIdenticalRemoteBookPockets(input);

            Assert.That(result, Is.EqualTo(input));
        }

        [Test]
        public void should_drop_only_bit_for_bit_identical_duplicate_pockets()
        {
            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "First",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-one" }
                }
            };

            var duplicate = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Duplicate Copy",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "audio-one" }
                }
            };

            var result = RefreshAuthorService.CoalesceIdenticalRemoteBookPockets(new List<Book> { first, duplicate });

            Assert.Multiple(() =>
            {
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(result[0], Is.SameAs(first));
            });
        }

        [Test]
        public void should_not_collapse_across_media_types()
        {
            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Audio",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "shared-edition" }
                }
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                Title = "Ebook",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1", // same provider token, different media type
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "shared-edition" }
                }
            };

            var input = new List<Book> { audiobook, ebook };

            var result = RefreshAuthorService.CoalesceIdenticalRemoteBookPockets(input);

            Assert.That(result.Count, Is.EqualTo(2));
        }
    }
}
