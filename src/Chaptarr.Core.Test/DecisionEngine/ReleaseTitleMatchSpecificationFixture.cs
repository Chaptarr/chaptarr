using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseTitleMatchSpecificationFixture
    {
        [Test]
        public void should_accept_interactive_search_result_when_release_title_omits_author_but_release_author_hint_matches()
        {
            var author = new Author { Name = "Gian Sardar" };
            var book = new Book { Title = "Land of Dreams" };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var release = new ReleaseInfo
            {
                Title = "Land of Dreams",
                Author = "Gian Sardar",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var logger = LogManager.GetCurrentClassLogger();
            var spec = new ReleaseTitleMatchSpecification(logger);

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.True);
        }


        [Test]
        public void should_accept_interactive_search_result_when_release_author_hint_contains_requested_author_among_coauthors()
        {
            var author = new Author { Name = "Brian Herbert" };
            var book = new Book { Title = "House Harkonnen" };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var release = new ReleaseInfo
            {
                Title = "House Harkonnen",
                Author = "Kevin J Anderson, Brian Herbert",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var logger = LogManager.GetCurrentClassLogger();
            var spec = new ReleaseTitleMatchSpecification(logger);

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_interactive_search_result_when_same_author_release_is_different_dune_book()
        {
            var (criteria, _) = BuildDukeOfCaladanCriteria();

            var release = new ReleaseInfo
            {
                Title = "House Harkonnen",
                Author = "Brian Herbert, Kevin J Anderson",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_reject_interactive_search_result_when_dune_universe_part_file_is_different_book()
        {
            var (criteria, _) = BuildDukeOfCaladanCriteria();

            var release = new ReleaseInfo
            {
                Title = "(Prequel to DUNE).Brian Herbert's & Kevin J Anderson's.\"du00.prq02.House Harkonnen.vol520+13.PAR2\".\"249\" of \"250\"",
                Author = "Brian Herbert, Kevin J Anderson",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_accept_interactive_search_result_when_release_author_missing_but_parsed_author_name_matches()
        {
            var author = new Author { Name = "Gian Sardar" };
            var book = new Book { Title = "Land of Dreams" };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var release = new ReleaseInfo
            {
                Title = "Land of Dreams",
                Author = null,
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { AuthorName = "Gian Sardar" }
            };

            var logger = LogManager.GetCurrentClassLogger();
            var spec = new ReleaseTitleMatchSpecification(logger);

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_interactive_search_result_when_release_omits_the_monitored_edition_subtitle()
        {
            var author = new Author { Name = "Mitch Albom" };
            var book = new Book
            {
                Title = "Tuesdays with Morrie",
                Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson",
                        Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                        Monitored = true
                    }
                }
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var release = new ReleaseInfo
            {
                Title = "Tuesdays with Morrie",
                Author = "Mitch Albom",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var logger = LogManager.GetCurrentClassLogger();
            var spec = new ReleaseTitleMatchSpecification(logger);

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
        }

        [Test]
        public void should_reject_interactive_search_result_when_release_author_hint_does_not_match_search_author()
        {
            var author = new Author { Name = "Clive Cussler" };
            var book = new Book { Title = "The Grey Ghost" };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var release = new ReleaseInfo
            {
                Title = "The Grey Ghost",
                Author = "Lila Grey",
                PublishDate = DateTime.UtcNow
            };

            var remoteBook = new RemoteBook
            {
                Release = release
            };

            var logger = LogManager.GetCurrentClassLogger();
            var spec = new ReleaseTitleMatchSpecification(logger);

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_not_emit_title_mismatch_when_pack_detector_already_classified_multi_book_release()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book { Title = "Red Rising" };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };

            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Red Rising Series - Books 1 - 4",
                    Author = "Pierce Brown",
                    PublishDate = DateTime.UtcNow
                },
                PackDetection = new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.MultipleBooks
                }
            };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_accept_single_target_rss_when_series_label_shares_first_book_title(bool includeSeriesName)
        {
            var (author, targetBook, _) = BuildMattDinnimanCatalog(includeSeriesName);

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { targetBook },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = "Matt Dinniman" },
                Release = new ReleaseInfo
                {
                    Title = "Matt Dinniman-[Dungeon Crawler Carl 08]-Parade of Horribles [epub mobi]",
                    Author = "Matt Dinniman",
                    PublishDate = DateTime.UtcNow
                }
            };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_single_target_rss_when_release_title_matches_adjacent_sibling()
        {
            var (author, _, siblingBook) = BuildMattDinnimanCatalog(includeSeriesName: true);
            var targetBook = author.Books.Find(book => book.Title == "Dungeon Crawler Carl");

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { targetBook },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = "Matt Dinniman" },
                Release = new ReleaseInfo
                {
                    Title = "Matt Dinniman Dungeon Crawler Carl Carl's Doomsday Scenario",
                    Author = "Matt Dinniman",
                    PublishDate = DateTime.UtcNow
                }
            };

            author.Books = new List<Book> { targetBook, siblingBook };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Release appears to match a different book by this author"));
        }

        [Test]
        public void should_skip_fuzzy_title_validation_for_rss_when_multiple_books_are_mapped()
        {
            var (author, targetBook, siblingBook) = BuildMattDinnimanCatalog(includeSeriesName: true);

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { targetBook, siblingBook },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = "Matt Dinniman" },
                Release = new ReleaseInfo
                {
                    Title = "Matt Dinniman Dungeon Crawler Carl Carl's Doomsday Scenario",
                    Author = "Matt Dinniman",
                    PublishDate = DateTime.UtcNow
                }
            };

            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase(BookMatchingStrictness.Aggressive, true)]
        [TestCase(BookMatchingStrictness.Balanced, true)]
        [TestCase(BookMatchingStrictness.Strict, false)]
        public void should_not_treat_a_subtitle_number_as_a_series_position_outside_strict(BookMatchingStrictness strictness, bool expectedAccepted)
        {
            var author = new Author { Name = "George R.R. Martin" };
            var book = new Book
            {
                Author = author,
                Title = "Fire & Blood",
                SeriesName = "A Targaryen History",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Fire & Blood", Monitored = true }
                }
            };
            author.Books = new List<Book> { book };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };
            var release = new ReleaseInfo
            {
                Title = "Fire & Blood: 300 Years Before A Game of Thrones",
                Author = "George R.R. Martin",
                PublishDate = DateTime.UtcNow
            };
            var match = ReleaseTitleMatchScorer.FindBestMatch(release.Title, author.Name, new[] { book }, release.Author, author.Books);
            var remoteBook = new RemoteBook { Release = release, SearchCriteriaMatch = match };
            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger(), ConfigServiceTestProxy.Create(strictness));

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.Multiple(() =>
            {
                Assert.That(match.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SuspiciousAdjacentNumber));
                Assert.That(decision.Accepted, Is.EqualTo(expectedAccepted));
            });
        }

        [TestCase(BookMatchingStrictness.Aggressive, true)]
        [TestCase(BookMatchingStrictness.Balanced, true)]
        [TestCase(BookMatchingStrictness.Strict, false)]
        public void should_only_position_veto_a_target_series_label_in_strict_mode(BookMatchingStrictness strictness, bool expectedAccepted)
        {
            var author = new Author { Name = "Matt Dinniman" };
            var book = new Book
            {
                Author = author,
                Title = "Dungeon Crawler Carl",
                SeriesName = "Dungeon Crawler Carl",
                SeriesPosition = "1"
            };
            author.Books = new List<Book> { book };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };
            var release = new ReleaseInfo
            {
                Title = "Matt Dinniman - Dungeon Crawler Carl 2",
                Author = "Matt Dinniman",
                PublishDate = DateTime.UtcNow
            };
            var match = ReleaseTitleMatchScorer.FindBestMatch(release.Title, author.Name, new[] { book }, release.Author, author.Books);
            var remoteBook = new RemoteBook { Release = release, SearchCriteriaMatch = match };
            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger(), ConfigServiceTestProxy.Create(strictness));

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.Multiple(() =>
            {
                Assert.That(match.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SeriesPositionMismatch));
                Assert.That(decision.Accepted, Is.EqualTo(expectedAccepted));
            });
        }

        [Test]
        public void should_not_ignore_numeric_residue_when_match_has_a_nonnumeric_problem()
        {
            var match = new TitleMatchResult
            {
                IsMatch = false,
                ProblemCode = TitleMatchProblemCode.SuspiciousAdjacentNumber,
                Problems = new List<TitleMatchProblem>
                {
                    new TitleMatchProblem { Code = TitleMatchProblemCode.SuspiciousAdjacentNumber, Value = "3" },
                    new TitleMatchProblem { Code = TitleMatchProblemCode.SiblingTitleContradiction, Value = "Other Title" }
                }
            };
            var identity = new ReleaseIdentityEvidence { HasPositiveIdentityEvidence = true };

            var accepted = ReleaseTitleMatchSpecification.IsAcceptedMatch(
                match,
                identity,
                BookMatchingStrictness.Balanced);

            Assert.That(accepted, Is.False);
        }

        [Test]
        public void should_not_treat_a_numeric_file_part_range_as_a_series_position_in_strict_mode()
        {
            var author = new Author { Name = "Neil Gaiman" };
            var book = new Book
            {
                Author = author,
                Title = "American Gods",
                SeriesName = "American Gods",
                SeriesPosition = "1"
            };
            author.Books = new List<Book> { book };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true
            };
            var release = new ReleaseInfo
            {
                Title = "\"Neil Gaiman - American Gods.11-14.mp3\" (64kb) [182/336]",
                Author = "Neil Gaiman",
                PublishDate = DateTime.UtcNow
            };
            var match = ReleaseTitleMatchScorer.FindBestMatch(release.Title, author.Name, new[] { book }, release.Author, author.Books);
            var remoteBook = new RemoteBook { Release = release, SearchCriteriaMatch = match };
            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger(), ConfigServiceTestProxy.Create(BookMatchingStrictness.Strict));

            var decision = spec.IsSatisfiedBy(remoteBook, criteria);

            Assert.Multiple(() =>
            {
                Assert.That(match.ProblemCode, Is.EqualTo(TitleMatchProblemCode.None));
                Assert.That(decision.Accepted, Is.True);
            });
        }

        private static (BookSearchCriteria Criteria, Book TargetBook) BuildDukeOfCaladanCriteria()
        {
            var author = new Author { Name = "Brian Herbert" };
            var targetBook = new Book
            {
                Id = 7099,
                Title = "Dune: The Duke of Caladan",
                Author = author,
                SeriesName = "The Caladan Trilogy",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Dune: The Duke of Caladan",
                        Monitored = true
                    }
                }
            };

            var siblingBook = new Book
            {
                Id = 4782,
                Title = "House Harkonnen",
                Author = author,
                SeriesName = "Prelude to Dune",
                SeriesPosition = "2",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 2,
                        Title = "House Harkonnen",
                        Monitored = true
                    }
                }
            };

            author.Books = new List<Book> { targetBook, siblingBook };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { targetBook },
                InteractiveSearch = true
            };

            return (criteria, targetBook);
        }

        private static (Author Author, Book TargetBook, Book SiblingBook) BuildMattDinnimanCatalog(bool includeSeriesName)
        {
            var author = new Author
            {
                Id = 40,
                Name = "Matt Dinniman"
            };

            var targetBook = new Book
            {
                Id = 1933,
                Title = "Parade of Horribles",
                Author = author,
                AuthorId = author.Id,
                SeriesName = includeSeriesName ? "Dungeon Crawler Carl" : null,
                SeriesPosition = includeSeriesName ? "8" : null,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Parade of Horribles", Monitored = true }
                }
            };

            var siblingBook = new Book
            {
                Id = 1921,
                Title = "Carl's Doomsday Scenario",
                Author = author,
                AuthorId = author.Id,
                SeriesName = "Dungeon Crawler Carl",
                SeriesPosition = "2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Title = "Carl's Doomsday Scenario", Monitored = true }
                }
            };

            var seriesBook = new Book
            {
                Id = 1920,
                Title = "Dungeon Crawler Carl",
                Author = author,
                AuthorId = author.Id,
                SeriesName = "Dungeon Crawler Carl",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 3, Title = "Dungeon Crawler Carl", Monitored = true }
                }
            };

            author.Books = new List<Book> { seriesBook, targetBook, siblingBook };

            return (author, targetBook, siblingBook);
        }
    }
}
