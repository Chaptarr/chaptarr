using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListExclusionBookMatcherFixture
    {
        [Test]
        public void canonical_ids_should_only_include_provider_ids()
        {
            var book = new Book
            {
                HardcoverBookId = "123",
                GoodreadsBookId = "gr:456",
                GoodreadsWorkId = "789",
                OpenLibraryEditionId = "OL1M",
                OpenLibraryWorkId = "ol:OL2W",
                GoogleBooksId = "gb:book-1",
                ASIN = "B00TEST123",
                AudibleASIN = "az:B00TEST999",
                RemoteProviderIds = new HashSet<string> { "gr:999", "hc:888" },
                ISBN10 = "0123456789",
                ISBN13 = "9780123456789"
            };

            var result = ImportListExclusionBookMatcher.GetCanonicalProviderIds(book);

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("hc:123"));
                Assert.That(result, Does.Contain("gr:456"));
                Assert.That(result, Does.Contain("gr:789"));
                Assert.That(result, Does.Contain("ol:OL1M"));
                Assert.That(result, Does.Contain("ol:OL2W"));
                Assert.That(result, Does.Contain("gb:book-1"));
                Assert.That(result, Does.Contain("az:B00TEST123"));
                Assert.That(result, Does.Contain("az:B00TEST999"));
                Assert.That(result, Does.Contain("gr:999"));
                Assert.That(result, Does.Contain("hc:888"));
                Assert.That(result.Any(id => id.Contains("0123456789")), Is.False);
                Assert.That(result.Any(id => id.Contains("9780123456789")), Is.False);
            });
        }

        [Test]
        public void lookup_ids_should_include_legacy_raw_ids_and_isbns()
        {
            var book = new Book
            {
                HardcoverBookId = "hc:123",
                ASIN = "az:B00TEST123",
                ISBN10 = "0123456789",
                ISBN13 = "9780123456789"
            };

            var result = ImportListExclusionBookMatcher.GetLookupIds(book);

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("hc:123"));
                Assert.That(result, Does.Contain("123"));
                Assert.That(result, Does.Contain("az:B00TEST123"));
                Assert.That(result, Does.Contain("B00TEST123"));
                Assert.That(result, Does.Contain("0123456789"));
                Assert.That(result, Does.Contain("9780123456789"));
            });
        }

        [Test]
        public void applies_to_book_should_respect_media_type()
        {
            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123"
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:123"
            };

            var exclusion = new ImportListExclusion
            {
                ForeignId = "hc:123",
                MediaType = BookMediaType.Audiobook
            };

            Assert.Multiple(() =>
            {
                Assert.That(ImportListExclusionBookMatcher.AppliesToBook(exclusion, audiobook), Is.True);
                Assert.That(ImportListExclusionBookMatcher.AppliesToBook(exclusion, ebook), Is.False);
            });
        }

        [Test]
        public void applies_to_provider_id_should_require_matching_media_when_known()
        {
            var exclusion = new ImportListExclusion
            {
                ForeignId = "gr:123",
                MediaType = BookMediaType.Audiobook
            };

            Assert.Multiple(() =>
            {
                Assert.That(ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, "gr:123", BookMediaType.Audiobook), Is.True);
                Assert.That(ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, "gr:123", BookMediaType.Ebook), Is.False);
                Assert.That(ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, "gr:123", null), Is.False);
            });
        }

        [Test]
        public void applies_to_book_should_match_remote_provider_aliases()
        {
            var book = new Book
            {
                MediaType = BookMediaType.Audiobook,
                RemoteProviderIds = new HashSet<string> { "gr:3046572" }
            };

            var exclusion = new ImportListExclusion
            {
                ForeignId = "gr:3046572",
                MediaType = BookMediaType.Audiobook
            };

            Assert.That(ImportListExclusionBookMatcher.AppliesToBook(exclusion, book), Is.True);
        }
    }
}
