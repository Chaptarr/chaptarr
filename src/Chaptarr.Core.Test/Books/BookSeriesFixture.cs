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

        private static SeriesBookLink Link(string title, string id, string position, BookMediaType mediaType) => new SeriesBookLink
        {
            Position = position,
            IsPrimary = true,
            Series = new Series { Title = title, GoodreadsSeriesId = id, MediaType = mediaType }
        };
    }
}
