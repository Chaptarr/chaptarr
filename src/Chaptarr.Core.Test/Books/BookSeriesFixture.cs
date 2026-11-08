using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookSeriesFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void api_should_display_all_paired_memberships_instead_of_the_scalar(BookMediaType mediaType)
        {
            var book = new Book { Id = 1, Title = "The Lost Metal", HardcoverBookId = "hc:427863", MediaType = mediaType,
                SeriesName = "Mistborn, Era 2: Wax & Wayne", SeriesPosition = "16" };
            book.SeriesLinks = new List<SeriesBookLink>
            {
                Link("The Cosmere", "gr:135117", "16", mediaType),
                Link("Mistborn, Era 2: Wax & Wayne", "gr:123082", "4", mediaType)
            };
            Assert.That(book.ToResource().SeriesTitle, Is.EqualTo("Mistborn, Era 2: Wax & Wayne #4; The Cosmere #16"));
            Assert.That(BookSeries.Select(book).Position, Is.EqualTo("4"));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void no_membership_must_not_resurrect_unbacked_scalar_or_another_media_side(BookMediaType mediaType)
        {
            var otherMedia = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var book = new Book { Id = 1, Title = "Standalone", MediaType = mediaType, SeriesName = "Unbacked series", SeriesPosition = "2",
                SeriesLinks = new List<SeriesBookLink> { Link("Other side", "gr:1", "1", otherMedia), Link("Other provider", null, "2", mediaType) } };
            Assert.That(BookSeries.Select(book), Is.Null);
            Assert.That(book.ToResource().SeriesTitle, Is.Null.Or.Empty);
        }

        [TestCase("1-3", "1")]
        [TestCase("3.5,4.5,6.7", "3.5")]
        [TestCase("9.1", "9.1")]
        [TestCase("0.25", "0.25")]
        [TestCase("序章", null)]
        [SetCulture("fr-FR")]
        public void presentation_order_must_preserve_the_provider_position(string position, string expectedNumber)
        {
            var link = Link("Series", "gr:1", position, BookMediaType.Audiobook);
            var book = new Book { SeriesLinks = new List<SeriesBookLink> { link } };
            Assert.That(BookSeries.Select(book).Position, Is.EqualTo(position));
            Assert.That(BookSeries.PositionOrder(position), Is.EqualTo(expectedNumber == null ? decimal.MaxValue : decimal.Parse(expectedNumber, System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Test]
        public void decimal_positions_must_not_sort_as_zero_and_primary_selection_must_keep_its_pair()
        {
            var book = new Book { SeriesLinks = new List<SeriesBookLink>
            {
                Link("Split-volume", "gr:213759", "9.1", BookMediaType.Audiobook),
                Link("La caduta di Malazan", "gr:95257", "9", BookMediaType.Audiobook)
            } };
            Assert.That(BookSeries.Select(book).Position, Is.EqualTo("9"));
            book.SeriesLinks[1].IsPrimary = false;
            Assert.That(BookSeries.Select(book).Position, Is.EqualTo("9.1"));
            Assert.That(BookSeries.GetLinks(book).Count(), Is.EqualTo(2));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void unknown_primary_should_keep_membership_without_outranking_explicit_primary(BookMediaType mediaType)
        {
            var smoke = Link("Smoke Trilogy", "gr:43854", "4.5", mediaType);
            smoke.IsPrimary = null;
            var henry = Link("Henry Fitzroy", "gr:84745", "10.5", mediaType);
            henry.IsPrimary = false;
            var book = new Book { Title = "Paranormal Payback", GoodreadsWorkId = "gr:254958892", MediaType = mediaType,
                SeriesLinks = new List<SeriesBookLink> { henry, smoke } };

            Assert.That(BookSeries.Select(book), Is.SameAs(smoke), "unknown and secondary retain the existing position order");
            Assert.That(book.ToResource().SeriesTitle, Is.EqualTo("Smoke Trilogy #4.5; Henry Fitzroy #10.5"));
            henry.IsPrimary = true;
            Assert.That(BookSeries.Select(book), Is.SameAs(henry));
            Assert.That(BookSeries.GetLinks(book).Count(), Is.EqualTo(2));
            Assert.That(smoke.IsPrimary, Is.Null);
        }

        [Test]
        public void primary_selection_should_prefer_true_then_unknown_then_false_before_position_order()
        {
            var explicitSecondary = Link("Almost Royal", "gr:almost-royal", "1.5", BookMediaType.Audiobook);
            explicitSecondary.IsPrimary = false;
            var unknown = Link("Risking It All", "gr:risking-it-all", "3.5", BookMediaType.Audiobook);
            unknown.IsPrimary = null;
            var explicitPrimary = Link("Primary Example Series", "gr:primary-example-series", "8", BookMediaType.Audiobook);
            explicitPrimary.IsPrimary = true;
            var book = new Book
            {
                Title = "My Gift Is You",
                MediaType = BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink> { explicitSecondary, unknown, explicitPrimary }
            };

            Assert.That(BookSeries.Select(book), Is.SameAs(explicitPrimary));

            explicitPrimary.IsPrimary = false;
            Assert.That(BookSeries.Select(book), Is.SameAs(unknown));

            unknown.IsPrimary = false;
            Assert.That(BookSeries.Select(book), Is.SameAs(explicitSecondary));
        }

        [Test]
        public void primary_selection_should_keep_existing_position_and_id_tie_order_with_equal_primary_metadata()
        {
            var later = Link("Later", "gr:later", "3", BookMediaType.Ebook);
            later.IsPrimary = null;
            var earlier = Link("Earlier", "gr:earlier", "1", BookMediaType.Ebook);
            earlier.IsPrimary = null;
            var book = new Book
            {
                Title = "Example",
                MediaType = BookMediaType.Ebook,
                SeriesLinks = new List<SeriesBookLink> { later, earlier }
            };

            Assert.That(BookSeries.Select(book), Is.SameAs(earlier));

            var higherId = Link("Same Position", "gr:z", "1", BookMediaType.Ebook);
            higherId.IsPrimary = null;
            var lowerId = Link("Same Position", "gr:a", "1", BookMediaType.Ebook);
            lowerId.IsPrimary = null;
            book.SeriesLinks = new List<SeriesBookLink> { higherId, lowerId };

            Assert.That(BookSeries.Select(book), Is.SameAs(lowerId));
        }

        [Test]
        public void primary_selection_should_ignore_primary_links_from_the_other_media_side()
        {
            var otherSidePrimary = Link("Ebook Series", "gr:ebook", "1", BookMediaType.Ebook);
            otherSidePrimary.IsPrimary = true;
            var audiobookUnknown = Link("Audiobook Series", "gr:audiobook", "4", BookMediaType.Audiobook);
            audiobookUnknown.IsPrimary = null;
            var book = new Book
            {
                Title = "Example",
                MediaType = BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink> { otherSidePrimary, audiobookUnknown }
            };

            Assert.That(BookSeries.Select(book), Is.SameAs(audiobookUnknown));
        }

        [Test]
        public void api_title_cleanup_must_compare_individual_memberships_not_the_joined_label()
        {
            var book = new Book { Title = "Guards! Guards! (Discworld, #8)", SeriesLinks = new List<SeriesBookLink>
            {
                Link("City Watch", "gr:106221", "1", BookMediaType.Audiobook),
                Link("Discworld", "gr:40650", "8", BookMediaType.Audiobook)
            } };
            Assert.That(book.ToResource().Title, Is.EqualTo("Guards! Guards!"));
            Assert.That(book.ToResource().SeriesTitle, Is.EqualTo("City Watch #1; Discworld #8"));
        }

        [Test]
        public void metadata_copy_must_not_mix_series_or_retain_removed_components()
        {
            var book = new Book { SeriesId = 5, SeriesName = "The Cosmere", SeriesPosition = "6" };
            var remote = new Book { SeriesName = "The Stormlight Archive" };
            book.UseMetadataFrom(remote);
            Assert.That(book.SeriesName, Is.EqualTo(remote.SeriesName));
            Assert.That(book.SeriesPosition, Is.Null);
            Assert.That(book.SeriesId, Is.Null);
            remote.SeriesName = null;
            book.UseMetadataFrom(remote);
            Assert.That(book.SeriesName, Is.Null);
            book.UseMetadataFrom(remote);
            Assert.That(book.SeriesName, Is.Null);
        }

        [Test]
        public void header_series_should_use_provider_identity_and_keep_the_membership_position(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values("49075", "gr:49075", " GR:49075 ")] string providerId)
        {
            var header = Link("The Stormlight Archive", providerId, "1", mediaType);
            header.SeriesId = 23;
            header.Series.Value.Id = 23;
            header.IsPrimary = false;
            var fallback = Link("The Cosmere", "gr:135117", "0.5", mediaType);
            fallback.SeriesId = 49075;
            fallback.Series.Value.Id = 49075;
            var book = new Book
            {
                SeriesId = 49075, SeriesName = "Stale scalar", SeriesPosition = "99", MediaType = mediaType,
                SeriesLinks = new List<SeriesBookLink> { fallback, header }
            };

            Assert.That(BookSeries.Select(book), Is.SameAs(header));
            Assert.That(BookSeries.GetHeaderLink(book), Is.SameAs(header));
            Assert.That(BookSeries.Label(BookSeries.Select(book)), Is.EqualTo("The Stormlight Archive #1"));
            Assert.That(BookSeries.GetLinks(book), Is.EqualTo(new[] { fallback, header }), "All-membership presentation order is unchanged.");
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void missing_or_unavailable_header_series_should_keep_the_existing_fallback(BookMediaType mediaType)
        {
            var otherMedia = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var primary = Link("Primary", "gr:20", "8", mediaType);
            var unknown = Link("Unknown", "gr:30", "3", mediaType);
            unknown.IsPrimary = null;
            var secondary = Link("Secondary", "gr:40", "1", mediaType);
            secondary.IsPrimary = false;
            var otherSide = Link("Other side", "gr:10", "0", otherMedia);
            var book = new Book
            {
                MediaType = mediaType,
                SeriesLinks = new List<SeriesBookLink> { secondary, unknown, otherSide, primary }
            };

            foreach (var headerId in new int?[] { null, 0, 999, 10 })
            {
                book.SeriesId = headerId;
                Assert.That(BookSeries.GetHeaderLink(book), Is.Null);
                primary.IsPrimary = true;
                unknown.IsPrimary = null;
                Assert.That(BookSeries.Select(book), Is.SameAs(primary));
                primary.IsPrimary = false;
                Assert.That(BookSeries.Select(book), Is.SameAs(unknown));
                unknown.IsPrimary = false;
                Assert.That(BookSeries.Select(book), Is.SameAs(secondary));
            }
            book.SeriesLinks.Clear();
            Assert.That(BookSeries.Select(book), Is.Null);
            Assert.That(BookSeries.Select(null), Is.Null);
            Assert.That(BookSeries.GetHeaderLink(null), Is.Null);
        }

        [Test]
        public void api_should_show_only_a_valid_header_but_retain_all_labels_for_filtering_and_title_cleanup(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(null, 106221, 40650, 999)] int? headerId)
        {
            var opposite = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var book = new Book { Title = "Guards! Guards! (Discworld, #8)", MediaType = mediaType, SeriesId = headerId,
                SeriesLinks = new List<SeriesBookLink>
                {
                    Link("Discworld", "40650", "8", mediaType),
                    Link("City Watch", "gr:106221", "1", mediaType),
                    Link("Other side", "gr:999", "0", opposite)
                } };
            var resource = book.ToResource();
            Assert.Multiple(() =>
            {
                Assert.That(resource.SeriesTitle, Is.EqualTo(headerId switch
                {
                    106221 => "City Watch #1",
                    40650 => "Discworld #8",
                    _ => "City Watch #1; Discworld #8"
                }));
                Assert.That(resource.SeriesLabels, Is.EqualTo(new[] { "City Watch #1", "Discworld #8" }));
                Assert.That(resource.Title, Is.EqualTo("Guards! Guards!"));
            });
            new BookResource { SeriesTitle = "Submitted title", SeriesLabels = new List<string> { "Submitted label" } }.ToModel(book);
            Assert.That(book.ToResource().SeriesLabels, Is.EqualTo(new[] { "City Watch #1", "Discworld #8" }));
        }

        [TestCase("hc:49075")]
        [TestCase("gr:[49075]")]
        [TestCase("gr:49075:other")]
        public void unusable_membership_identity_must_not_select_a_header_or_break_the_existing_fallback(string providerId)
        {
            var fallback = Link("Fallback", "gr:20", "1", BookMediaType.Audiobook);
            var book = new Book { SeriesId = 49075, SeriesLinks = new List<SeriesBookLink>
                { fallback, Link("Unusable header", providerId, "2", BookMediaType.Audiobook) } };
            Assert.That(BookSeries.GetHeaderLink(book), Is.Null);
            Assert.That(BookSeries.Select(book), Is.SameAs(fallback));
        }

        private static SeriesBookLink Link(string title, string id, string position, BookMediaType mediaType) => new SeriesBookLink
        {
            Position = position,
            IsPrimary = true,
            Series = new Series { Title = title, GoodreadsSeriesId = id, MediaType = mediaType }
        };
    }
}
