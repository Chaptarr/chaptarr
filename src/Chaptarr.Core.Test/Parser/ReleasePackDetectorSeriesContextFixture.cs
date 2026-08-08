using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

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
