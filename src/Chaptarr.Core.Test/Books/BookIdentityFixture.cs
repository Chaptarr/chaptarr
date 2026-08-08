using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookIdentityFixture
    {
        [Test]
        public void stable_work_tokens_should_exclude_internal_base_book_id()
        {
            var book = new Book
            {
                BaseBookId = "sms-pocket-123",
                HardcoverBookId = "hc:42",
                GoodreadsWorkId = "gr:99",
                OpenLibraryWorkId = "ol:OL123W"
            };

            var tokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);

            Assert.That(tokens, Is.EquivalentTo(new[] { "hc:42", "gr:99", "ol:OL123W" }));
            Assert.That(tokens, Does.Not.Contain("sms-pocket-123"));
        }

        [Test]
        public void stable_work_tokens_should_exclude_provider_shaped_base_book_id()
        {
            var book = new Book
            {
                BaseBookId = "hc:42"
            };

            var tokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);

            Assert.That(tokens, Is.Empty);
        }

        [Test]
        public void stable_work_tokens_should_not_include_edition_level_ids()
        {
            var book = new Book
            {
                GoogleBooksId = "gb:abc",
                ASIN = "B000TEST",
                AudibleASIN = "B000TEST",
                RemoteProviderIds = new HashSet<string>
                {
                    "az:B000TEST",
                    "gb:abc",
                    "hc:edition:123"
                },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        GoogleBooksEditionId = "gb:edition-1",
                        Asin = "B000TEST"
                    }
                }
            };

            var tokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);

            Assert.That(tokens, Is.Empty);
        }

        [Test]
        public void stable_work_tokens_should_include_stable_remote_provider_aliases()
        {
            var book = new Book
            {
                RemoteProviderIds = new HashSet<string>
                {
                    "hc:42",
                    "gr:99",
                    "ol:OL123W",
                    "az:B000TEST",
                    "gb:abc",
                    "hc:edition:123",
                    "raw-id"
                }
            };

            var tokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);

            Assert.That(tokens, Is.EquivalentTo(new[] { "hc:42", "gr:99", "ol:OL123W" }));
        }

        [Test]
        public void provider_identity_match_should_allow_same_media_edition_only_pockets()
        {
            var local = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Asin = "B000TEST" }
                }
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { AudibleASIN = "B000TEST" }
                }
            };

            Assert.That(BookIdentity.MatchesByProviderIdIntersection(local, remote), Is.True);
        }

        [Test]
        public void work_first_result_should_retain_ambiguous_edition_only_candidates_without_blessing_them_as_matches()
        {
            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Asin = "B000TEST" } }
            };
            var second = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { AudibleASIN = "B000TEST" } }
            };
            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Asin = "B000TEST" } }
            };

            var result = BookIdentity.FindWorkFirstMatchResult(new[] { first, second }, remote);

            Assert.That(result.Disposition, Is.EqualTo(WorkFirstMatchDisposition.EditionAmbiguous));
            Assert.That(result.Matches, Is.EquivalentTo(new[] { first, second }));
            Assert.That(BookIdentity.FindWorkFirstMatches(new[] { first, second }, remote), Is.Empty,
                "legacy/general callers must continue to fail closed on shared edition identity");
        }

        [Test]
        public void provider_identity_match_should_not_bridge_edition_id_to_work_id_row()
        {
            var workBacked = new Book
            {
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:work-1",
                Editions = new List<Edition>
                {
                    new Edition { Asin = "B000TEST" }
                }
            };

            var asinOnly = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Asin = "B000TEST" }
                }
            };

            Assert.That(BookIdentity.MatchesByProviderIdIntersection(workBacked, asinOnly), Is.False);
        }

        [Test]
        public void provider_identity_match_should_not_use_edition_id_across_media_types()
        {
            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Asin = "B000TEST" }
                }
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition { Asin = "B000TEST" }
                }
            };

            Assert.That(BookIdentity.MatchesByProviderIdIntersection(audiobook, ebook), Is.False);
        }

        [Test]
        public void trusted_foreign_edition_id_should_ignore_local_and_isbn_values()
        {
            var edition = new Edition
            {
                ForeignEditionId = "0_edition",
                Isbn13 = "9780000000000",
                Isbn10 = "0000000000"
            };

            Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(edition), Is.Null);
        }

        [Test]
        public void trusted_foreign_edition_id_should_ignore_ambiguous_legacy_scalars()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { ForeignEditionId = "hc:12345" }), Is.Null);
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { ForeignEditionId = "gr:67890" }), Is.Null);
            });
        }

        [Test]
        public void trusted_foreign_edition_id_should_use_only_hardcover_goodreads_or_az_identity()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { HardcoverEditionId = "12345" }), Is.EqualTo("hc:edition:12345"));
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { GoodreadsEditionId = 67890 }), Is.EqualTo("gr:67890"));
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { Asins = new List<string> { "b00abc1234" } }), Is.EqualTo("az:B00ABC1234"));
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { ForeignEditionId = "hc:edition:12345" }), Is.EqualTo("hc:edition:12345"));
                Assert.That(BookEditionIdentity.GetTrustedForeignEditionId(new Edition { ForeignEditionId = "az:b00abc1234" }), Is.EqualTo("az:B00ABC1234"));
            });
        }

        [Test]
        public void readarr_facade_hardcover_edition_id_should_parse_typed_and_foreign_ids()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookEditionIdentity.GetReadarrFacadeHardcoverEditionId(new Edition { HardcoverEditionId = "30643037" }), Is.EqualTo("30643037"));
                Assert.That(BookEditionIdentity.GetReadarrFacadeHardcoverEditionId(new Edition { ForeignEditionId = "hc:edition:30643037-ebook" }), Is.EqualTo("30643037"));
                Assert.That(BookEditionIdentity.GetReadarrFacadeHardcoverEditionId(new Edition { ForeignEditionId = "gr:234547961-ebook" }), Is.Null);
            });
        }

        [Test]
        public void readarr_facade_goodreads_edition_id_should_parse_typed_and_foreign_ids()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookEditionIdentity.GetReadarrFacadeGoodreadsEditionId(new Edition { GoodreadsEditionId = 234547961 }), Is.EqualTo("234547961"));
                Assert.That(BookEditionIdentity.GetReadarrFacadeGoodreadsEditionId(new Edition { ForeignEditionId = "gr:234547961-audiobook" }), Is.EqualTo("234547961"));
                Assert.That(BookEditionIdentity.GetReadarrFacadeGoodreadsEditionId(new Edition { ForeignEditionId = "hc:edition:30643037-ebook" }), Is.Null);
            });
        }
    }
}
