using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseSearchSiblingCompetitionSpecFixture
    {
        private static Book Make(int id, Author author, string title, string series = "", string position = "", BookMediaType mediaType = BookMediaType.Ebook)
        {
            return new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title,
                SeriesName = series, SeriesPosition = position, MediaType = mediaType,
                HardcoverBookId = $"hc:{id}",
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true } }
            };
        }

        private static bool Accepted(Author author, Book target, List<Book> catalog, string releaseTitle, string structuredAuthor, string structuredBook = null)
        {
            var criteria = new BookSearchCriteria
            {
                Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog, InteractiveSearch = true
            };
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = releaseTitle, Author = structuredAuthor, Book = structuredBook, PublishDate = DateTime.UtcNow }
            };

            return new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(remoteBook, criteria).Accepted;
        }

        private static (Author Author, Book Target, List<Book> Catalog) JamesHunt()
        {
            var author = new Author { Id = 631, Name = "James Hunt" };
            var target = Make(42421, author, "Powerless", "A Tale Of Survival in a Powerless World", "1-4");
            var catalog = new List<Book>
            {
                target,
                Make(42407, author, "The Final Homestead", "EMP Survival in a Powerless World", "1"),
                Make(42454, author, "Broken Roads", "A Tale of Survival in a Powerless World", "2")
            };

            return (author, target, catalog);
        }

        [TestCase("James Hunt")]
        [TestCase(null)]
        public void a_sibling_title_should_reject_through_the_specification(string structuredAuthor)
        {
            var (author, target, catalog) = JamesHunt();

            Assert.That(Accepted(author, target, catalog, "James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)", structuredAuthor), Is.False);
            Assert.That(Accepted(author, target, catalog, "Broken Roads: A Tale of Survival in a Powerless World", structuredAuthor), Is.False);
            Assert.That(Accepted(author, target, catalog, "James Hunt - Powerless (epub)", structuredAuthor), Is.True);
        }

        [TestCase("James Hunt")]
        [TestCase(null)]
        public void a_forgivable_problem_on_the_target_must_not_skip_sibling_competition(string structuredAuthor)
        {
            var (author, target, catalog) = JamesHunt();

            // The target picks up a suspicious adjacent number here, which previously returned before
            // competition ran and let the specification's tolerance accept it anyway.
            Assert.That(Accepted(author, target, catalog, "James Hunt - Powerless 004 - The Final Homestead - EMP Survival in a Powerless World", structuredAuthor), Is.False);
        }

        [Test]
        public void an_issue_number_should_only_be_forgiven_with_identity_evidence()
        {
            var (author, target, catalog) = JamesHunt();

            // No author anywhere in the release: a numbered product from another publisher.
            Assert.That(Accepted(author, target, catalog, "Powerless.004.2017.digital.Son.of.Ultron-Empire", null), Is.False);

            // A structured author field asserting this author IS identity evidence, by rule.
            Assert.That(Accepted(author, target, catalog, "Powerless.004.2017.digital.Son.of.Ultron-Empire", "James Hunt"), Is.True);
        }

        [TestCase(BookMediaType.Audiobook, BookMediaType.Ebook)]
        [TestCase(BookMediaType.Ebook, BookMediaType.Audiobook)]
        public void only_siblings_in_the_same_media_enum_may_contradict(BookMediaType targetType, BookMediaType otherType)
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1", targetType);
            var sibling = Make(39408, author, "Dune Messiah", "Dune", "2", otherType);
            target.Editions[0].ReadingFormatId = 2;
            sibling.Editions[0].ReadingFormatId = 1;
            var catalog = new List<Book> { target, sibling };

            Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune Messiah", "Frank Herbert"), Is.True);
            Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune II", "Frank Herbert"), Is.True);

            sibling.MediaType = targetType;
            Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune Messiah", "Frank Herbert"), Is.False);
            Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune II", "Frank Herbert"), Is.False);
            Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune", "Frank Herbert"), Is.True);
        }

        [Test]
        public void structured_book_title_controls_the_final_decision_when_the_raw_title_disagrees()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1");
            var catalog = new List<Book>
            {
                target,
                Make(39408, author, "Dune Messiah", "Dune", "2"),
                Make(39409, author, "Children of Dune", "Dune", "3")
            };

            Assert.Multiple(() =>
            {
                Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune \"Dune 12.mp3\" MP3", "Frank Herbert", "Dune"), Is.True);
                Assert.That(Accepted(author, target, catalog, "Frank Herbert - Dune", "Frank Herbert", "Dune 3"), Is.False);
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void identical_edition_titles_in_the_same_media_enum_should_not_reject_a_shortened_release(BookMediaType mediaType)
        {
            var author = new Author { Id = 1, Name = "George S. Clason" };
            const string title = "The Richest Man in Babylon: Timeless Financial Wisdom for Today's Economy";
            var target = Make(1, author, title, mediaType: mediaType);
            var twin = Make(2, author, title, mediaType: mediaType);
            var catalog = new List<Book> { twin, target };

            Assert.That(Accepted(author, target, catalog, "George S. Clason - The Richest Man in Babylon", null), Is.True);

            twin.Editions[0].Title = "The Richest Man in Babylon: Study Guide";
            Assert.That(Accepted(author, target, catalog, "George S. Clason - The Richest Man in Babylon", null), Is.False);
        }

        [Test]
        public void a_known_narrator_or_series_must_not_forgive_an_issue_number()
        {
            var author = new Author { Id = 631, Name = "James Hunt" };
            var target = new Book
            {
                Id = 42421, AuthorId = author.Id, Author = author, Title = "Powerless",
                SeriesName = "A Tale Of Survival in a Powerless World", SeriesPosition = "1-4",
                MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:42421",
                Editions = new List<Edition>
                {
                    new Edition { Id = 42421, Title = "Powerless", Monitored = true, Narrator = "Scott Brick" }
                }
            };
            var catalog = new List<Book> { target };

            // A narrator at the boundary proves narration, not authorship.
            Assert.That(Accepted(author, target, catalog, "Powerless 004 Scott Brick", null), Is.False);

            // A known series at the boundary proves nothing about who wrote this release.
            Assert.That(Accepted(author, target, catalog, "A Tale Of Survival in a Powerless World Powerless 004", null), Is.False);

            // The author does prove it, from the title or from structured metadata.
            Assert.That(Accepted(author, target, catalog, "James Hunt - Powerless 004", null), Is.True);
            Assert.That(Accepted(author, target, catalog, "Powerless 004", "James Hunt"), Is.True);
        }

        [TestCase("Scott Brick - Powerless")]
        [TestCase("Powerless Scott Brick")]
        [TestCase("Read by Scott Brick - Powerless")]
        [TestCase("Powerless Read by Scott Brick")]
        [TestCase("Narrated by Scott Brick - Powerless")]
        [TestCase("Powerless Narrated by Scott Brick")]
        [TestCase("Powerless 004 Scott Brick")]
        [TestCase("Scott Brick 004 Powerless")]
        [TestCase("Scott; Brick - Powerless")]
        [TestCase("Powerless Scott; Brick")]
        public void known_narrator_without_author_cannot_pass_the_title_gate(string releaseTitle)
        {
            var author = new Author { Id = 631, Name = "James Hunt" };
            var target = Make(42421, author, "Powerless", "A Tale Of Survival in a Powerless World", "1-4");
            target.Editions[0].Narrator = "Scott Brick";

            Assert.That(Accepted(author, target, new List<Book> { target }, releaseTitle, structuredAuthor: null), Is.False);
        }

        [Test]
        public void a_sibling_title_inside_the_targets_own_title_should_still_be_accepted()
        {
            var author = new Author { Id = 9001, Name = "George R.R. Martin" };
            var target = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author, Title = "Fire & Blood: 300 Years Before A Game of Thrones",
                MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Fire & Blood: 300 Years Before A Game of Thrones", Monitored = true }
                }
            };
            var catalog = new List<Book>
            {
                target,
                new Book
                {
                    Id = 2, AuthorId = author.Id, Author = author, Title = "A Game of Thrones",
                    SeriesName = "A Song of Ice and Fire", SeriesPosition = "1",
                    MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:2",
                    Editions = new List<Edition> { new Edition { Id = 2, Title = "A Game of Thrones", Monitored = true } }
                }
            };

            Assert.That(Accepted(author, target, catalog, "George R.R. Martin - Fire & Blood: 300 Years Before A Game of Thrones", "George R.R. Martin"), Is.True);
        }

    }
}
