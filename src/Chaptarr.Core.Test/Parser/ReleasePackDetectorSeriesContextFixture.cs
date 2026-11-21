using System.Collections.Generic;
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
            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_series_label_with_position_and_book_title_as_multi_book_pack_when_target_title_omits_leading_article()
        {
            var targetBook = new Book { Title = "Parade of Horribles", SeriesName = "Dungeon Crawler Carl", SeriesPosition = "8" };

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_suffix_series_label_with_position_as_multi_book_pack()
        {
            var result = ReleasePackDetector.Detect(
                "Matt Dinniman - A Parade of Horribles (Dungeon Crawler Carl Book 8) [epub mobi]",
                null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_series_label_with_position_as_multi_book_pack_during_single_book_search()
        {
            var targetBook = new Book { Title = "Parade of Horribles", SeriesName = "Dungeon Crawler Carl", SeriesPosition = "8" };

            var result = ReleasePackDetector.Detect(
                "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_leave_adjacent_catalog_titles_without_structural_pack_wording_to_title_match_scorer()
        {
            var result = ReleasePackDetector.Detect(
                "Matt Dinniman Dungeon Crawler Carl Carl's Doomsday Scenario",
                null);

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
            var targetBook = new Book
            {
                Title = "Love at Stake Book 8",
                SeriesName = "Love at Stake",
                SeriesPosition = "8"
            };

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(result.PackType, Is.EqualTo("structured-series-range"));
        }

        [Test]
        public void should_not_treat_a_structured_series_range_as_a_pack_without_target_context()
        {
            var release = CreateStructuredRelease("Love at Stake", "01-16, 13.5");

            var result = ReleasePackDetector.Detect(release, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
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

            var result = ReleasePackDetector.Detect(release, null);

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

            var result = ReleasePackDetector.Detect(release, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None));
        }

        [Test]
        public void should_not_treat_the_target_books_complete_position_set_as_a_pack()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "5.5, 6.5, 7.5"
            };
            var release = CreateStructuredRelease("The Long Archive", "5.5, 6.5, 7.5");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_treat_an_unrelated_structured_series_range_as_a_pack_for_the_target()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "6"
            };
            var release = CreateStructuredRelease("The Other Archive", "1-8");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_detect_additional_positions_when_the_known_target_series_is_expanded_in_release_metadata()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "6"
            };
            var release = CreateStructuredRelease("the long archive", "1-8");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks),
                $"Type={result.PackType}; Match={result.MatchedValue}");
            Assert.That(result.PackType, Is.EqualTo("structured-series-range"));
        }

        [Test]
        public void should_not_infer_a_pack_when_the_target_series_position_is_unknown()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive"
            };
            var release = CreateStructuredRelease("The Long Archive", "1-8");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_infer_additional_positions_from_a_partially_parsed_target_position_set()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "6, unrecognized"
            };
            var release = CreateStructuredRelease("The Long Archive", "1-8");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_infer_a_pack_from_a_partially_parsed_release_position_set()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "6"
            };
            var release = CreateStructuredRelease("The Long Archive", "1-8, unrecognized");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_preserve_explicit_text_pack_detection_when_structured_positions_are_neutral()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                SeriesName = "The Long Archive",
                SeriesPosition = "5.5, 6.5, 7.5"
            };
            var release = CreateStructuredRelease("The Long Archive", "5.5, 6.5, 7.5");
            release.Title = "The Long Archive Books 1-8 Collection";

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks),
                $"Type={result.PackType}; Match={result.MatchedValue}");
            Assert.That(result.PackType, Is.EqualTo("book-range"));
        }

        [Test]
        public void should_use_same_media_series_link_as_target_context_when_book_summary_is_blank()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                MediaType = BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        Series = new Series
                        {
                            Title = "The Long Archive",
                            GoodreadsSeriesId = "gr:1",
                            MediaType = BookMediaType.Audiobook
                        },
                        Position = "5.5, 6.5, 7.5"
                    }
                }
            };
            var release = CreateStructuredRelease("The Long Archive", "5.5, 6.5, 7.5");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_use_a_series_link_from_the_other_media_type_as_target_context()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                MediaType = BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        Series = new Series
                        {
                            Title = "The Long Archive",
                            GoodreadsSeriesId = "gr:1",
                            MediaType = BookMediaType.Ebook
                        },
                        Position = "6"
                    }
                }
            };
            var release = CreateStructuredRelease("The Long Archive", "1-8");

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_match_structured_series_provider_id_to_a_goodreads_series_id()
        {
            var targetBook = new Book
            {
                Title = "Collected Stories",
                MediaType = BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        Series = new Series
                        {
                            Title = "The Long Archive",
                            GoodreadsSeriesId = "gr:1",
                            MediaType = BookMediaType.Audiobook
                        },
                        Position = "6"
                    }
                }
            };
            var release = CreateStructuredRelease("The Other Archive", "1-8");
            release.SeriesInfo[0].ProviderId = "gr:1";

            var result = ReleasePackDetector.Detect(release, targetBook);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.None),
                $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        private static ReleaseInfo CreateStructuredRelease(string seriesName, string position)
        {
            return new ReleaseInfo
            {
                Title = "Collected Stories",
                SeriesInfo = new List<ReleaseSeriesInfo>
                {
                    new ReleaseSeriesInfo { Name = seriesName, Position = position }
                }
            };
        }

    }
}
