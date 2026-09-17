using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleasePackDetectorSeriesContextFixture
    {
        [Test]
        public void should_not_treat_series_label_with_position_and_book_title_as_multi_book_pack_without_target_context()
        {
            var catalog = BuildMattDinnimanCatalog();

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                null,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_series_label_with_position_and_book_title_as_multi_book_pack_when_target_title_omits_leading_article()
        {
            var catalog = BuildMattDinnimanCatalog(targetTitle: "Parade of Horribles");

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                null,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_suffix_series_label_with_position_as_multi_book_pack()
        {
            var catalog = BuildMattDinnimanCatalog();

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman - A Parade of Horribles (Dungeon Crawler Carl Book 8) [epub mobi]",
                null,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_series_label_with_position_as_multi_book_pack_during_single_book_search()
        {
            var catalog = BuildMattDinnimanCatalog(targetTitle: "Parade of Horribles");
            var targetBook = catalog.Single(book => book.Title == "Parade of Horribles");

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                targetBook,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_leave_adjacent_catalog_titles_without_structural_pack_wording_to_title_match_scorer()
        {
            var catalog = BuildMattDinnimanCatalog();

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman Dungeon Crawler Carl Carl's Doomsday Scenario",
                null,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_treat_an_explicit_structured_series_range_as_a_multi_book_release()
        {
            var release = new ReleaseInfo
            {
                Title = "Love at Stake series",
                SeriesInfo = new List<ReleaseSeriesInfo>
                {
                    new ReleaseSeriesInfo
                    {
                        ProviderId = "67",
                        Name = "Love at Stake",
                        Position = "01-16, 13.5"
                    }
                }
            };

            var result = ReleasePackDetector.Detect(release, null, new List<Book>());

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(result.PackType, Is.EqualTo("structured-series-range"));
        }

        [Test]
        public void should_leave_single_structured_series_positions_to_title_matching()
        {
            var release = new ReleaseInfo
            {
                Title = "Dune",
                SeriesInfo = new List<ReleaseSeriesInfo>
                {
                    new ReleaseSeriesInfo
                    {
                        ProviderId = "12922",
                        Name = "Dune Chronicles",
                        Position = "1"
                    },
                    new ReleaseSeriesInfo
                    {
                        ProviderId = "28664",
                        Name = "Dune Universe",
                        Position = "10"
                    }
                }
            };

            var result = ReleasePackDetector.Detect(release, null, new List<Book>());

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None));
        }

        [Test]
        public void should_not_treat_a_date_position_as_a_structured_series_range()
        {
            var release = new ReleaseInfo
            {
                Title = "Marvel Week (2026-09-16)",
                SeriesInfo = new List<ReleaseSeriesInfo>
                {
                    new ReleaseSeriesInfo
                    {
                        ProviderId = "119524",
                        Name = "Marvel Comics Week",
                        Position = "2026-09-16"
                    }
                }
            };

            var result = ReleasePackDetector.Detect(release, null, new List<Book>());

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None));
        }

        private static List<Book> BuildMattDinnimanCatalog(string targetTitle = "A Parade of Horribles")
        {
            var author = new Author
            {
                Id = 40,
                Name = "Matt Dinniman"
            };

            return new List<Book>
            {
                new Book
                {
                    Id = 1920,
                    Title = "Dungeon Crawler Carl",
                    Author = author,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Audiobook,
                    HardcoverBookId = "hc:446681"
                },
                new Book
                {
                    Id = 1933,
                    Title = targetTitle,
                    Author = author,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Audiobook,
                    HardcoverBookId = "hc:1817542"
                },
                new Book
                {
                    Id = 1921,
                    Title = "Carl's Doomsday Scenario",
                    Author = author,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Audiobook,
                    HardcoverBookId = "hc:446680"
                }
            };
        }
    }
}
