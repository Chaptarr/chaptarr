using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookSeriesLabelFixture
    {
        private static SeriesBookLink Link(int seriesId, string title, string position, bool isPrimary = true, int workCount = 0)
        {
            return new SeriesBookLink
            {
                SeriesId = seriesId,
                Position = position,
                SeriesPosition = int.TryParse(position, out var parsed) ? parsed : 0,
                IsPrimary = isPrimary,
                Series = new Series
                {
                    Id = seriesId,
                    Title = title,
                    PrimaryWorkCount = workCount
                }
            };
        }

        [Test]
        public void should_take_title_and_position_from_the_same_link()
        {
            var links = new List<SeriesBookLink>
            {
                Link(2, "The Cosmere Universe", "6", workCount: 40),
                Link(1, "The Stormlight Archive", "1", workCount: 5)
            };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("The Stormlight Archive #1"));
        }

        [Test]
        public void should_prefer_the_narrower_series_over_the_umbrella()
        {
            var links = new List<SeriesBookLink>
            {
                Link(2, "The Cosmere Universe", "16", workCount: 40),
                Link(1, "Mistborn, Era 2: Wax & Wayne", "4", workCount: 4)
            };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("Mistborn, Era 2: Wax & Wayne #4"));
        }

        [Test]
        public void should_prefer_primary_slot_over_a_narrower_companion_series()
        {
            var links = new List<SeriesBookLink>
            {
                Link(2, "Companion Novellas", "1", isPrimary: false, workCount: 2),
                Link(1, "Malazan Book of the Fallen", "9", isPrimary: true, workCount: 10)
            };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("Malazan Book of the Fallen #9"));
        }

        [Test]
        public void should_prefer_a_link_that_carries_a_position()
        {
            var links = new List<SeriesBookLink>
            {
                Link(1, "Unnumbered Collection", null, workCount: 3),
                Link(2, "Red Rising Saga", "2", workCount: 6)
            };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("Red Rising Saga #2"));
        }

        [Test]
        public void should_omit_the_position_when_the_link_has_none()
        {
            var links = new List<SeriesBookLink> { Link(1, "Discworld", null) };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("Discworld"));
        }

        [Test]
        public void should_ignore_links_whose_series_has_no_title()
        {
            var links = new List<SeriesBookLink>
            {
                new SeriesBookLink { SeriesId = 1, Position = "1", IsPrimary = true },
                Link(2, "  ", "2"),
                Link(3, "Dresden Files", "3")
            };

            Assert.That(BookSeriesLabel.Build(links), Is.EqualTo("Dresden Files #3"));
        }

        [Test]
        public void should_return_null_when_there_are_no_links()
        {
            Assert.That(BookSeriesLabel.Build(null), Is.Null);
            Assert.That(BookSeriesLabel.Build(new List<SeriesBookLink>()), Is.Null);
        }

        [Test]
        public void should_pick_the_same_link_regardless_of_input_order()
        {
            var first = Link(7, "Same Size Series A", "1", workCount: 4);
            var second = Link(9, "Same Size Series B", "1", workCount: 4);

            var forward = BookSeriesLabel.Build(new List<SeriesBookLink> { first, second });
            var reversed = BookSeriesLabel.Build(new List<SeriesBookLink> { second, first });

            Assert.That(forward, Is.EqualTo(reversed));
        }

        [Test]
        public void format_should_join_title_and_position()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookSeriesLabel.Format("Discworld", "8"), Is.EqualTo("Discworld #8"));
                Assert.That(BookSeriesLabel.Format("Discworld", null), Is.EqualTo("Discworld"));
                Assert.That(BookSeriesLabel.Format("Discworld", "  "), Is.EqualTo("Discworld"));
                Assert.That(BookSeriesLabel.Format(null, "8"), Is.Null);
                Assert.That(BookSeriesLabel.Format("   ", "8"), Is.Null);
            });
        }
    }
}
