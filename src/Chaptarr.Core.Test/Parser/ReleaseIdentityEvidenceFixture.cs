using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NLog;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseIdentityEvidenceFixture
    {
        [TestCase("Fields of Promise")]
        [TestCase("Fields Promise")]
        public void series_cannot_replace_missing_author(string observedSeries)
        {
            var identity = Analyze(observedSeries + " - A Book Title", "Expected Reader", "A Book Title", "Fields of Promise", "");
            Assert.That(identity.HasAuthorIdentityEvidence, Is.False);
        }

        [Test]
        public void should_accept_exact_author_and_edition_title_with_shortened_series_and_matching_position()
        {
            var identity = Analyze(
                "Louise Penny - [Chief Inspector Gamache 16] - All the Devils Are Here (retail)",
                "Louise Penny",
                "All the Devils are Here",
                "Chief Inspector Armand Gamache",
                "16");

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
        }

        [Test]
        public void should_accept_series_name_when_only_a_structural_article_is_omitted()
        {
            var identity = Analyze(
                "Jim Butcher - [Dresden Files 1] - Storm Front",
                "Jim Butcher",
                "Storm Front",
                "The Dresden Files",
                "1");

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
        }

        [TestCase("Louise Penny - [Gamache 16] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Gamache] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Gamache 15] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Chief Inspector Armand Gamahce 16] - All the Devils Are Here")]
        [TestCase("Louise Penny - [The Chief Inspector Armand Gamache Mysteries 16] - All the Devils Are Here")]
        public void should_accept_concise_or_near_exact_series_evidence_when_the_other_identity_signals_agree(string releaseTitle)
        {
            var identity = Analyze(
                releaseTitle,
                "Louise Penny",
                "All the Devils are Here",
                "Chief Inspector Armand Gamache",
                "16");

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
        }

        [TestCase("")]
        [TestCase("Joanne Rowling")]
        public void should_accept_a_provider_author_alias_in_the_title_or_structured_author_field(string structuredAuthor)
        {
            var identity = Analyze(
                "Joanne Rowling - [Harry Potter 1] - Harry Potter and the Philosopher's Stone",
                "J.K. Rowling",
                "Harry Potter and the Philosopher's Stone",
                "Harry Potter",
                "1",
                new[] { "Joanne Rowling" },
                structuredAuthor);

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
        }

        [TestCase("John Smith", "Jane Jones", "Jane Jones", false, false)]
        [TestCase("John Smith", "Jane Jones", "John Smith", false, true)]
        [TestCase("John Smith", "Smith, John", "Smith, John", false, true)]
        [TestCase("John Smith", "Jane Jones", "Jane Jones", true, true)]
        public void sort_metadata_should_not_add_author_identity(string canonicalName, string sortName, string releaseAuthor, bool explicitPseudonym, bool hasAuthorEvidence)
        {
            var author = new Author
            {
                Name = canonicalName,
                SortName = sortName,
                Pseudonyms = explicitPseudonym ? new List<string> { releaseAuthor } : new List<string>()
            };
            var book = new Book { Author = author, Title = "A Book Title" };
            var identity = ReleaseIdentityEvidence.Analyze(
                new ReleaseInfo { Title = book.Title, Author = releaseAuthor }, author,
                new TitleMatchResult { MatchedVariant = book.Title, MatchedStart = 0, MatchedEnd = 2 });

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(hasAuthorEvidence));
                Assert.That(identity.HasStructuredAuthorMismatch, Is.EqualTo(!hasAuthorEvidence));
            });
        }

        [TestCase("Ann & Pat Smith", "Ann & Pat Smith", false, true)]
        [TestCase("Ann & Pat Smith", "Ann & Pat Smith", true, true)]
        [TestCase("Ann & Pat Smith", "Ann ＆ Pat Smith", false, true)]
        [TestCase("Ann & Pat Smith", "Ann ＆ Pat Smith", true, true)]
        [TestCase("Ann ＆ Pat Smith", "Ann & Pat Smith", false, true)]
        [TestCase("Ann ＆ Pat Smith", "Ann & Pat Smith", true, true)]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", false, false)]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", true, false)]
        [TestCase("Ann & Pat Smith", "Pat Smith", false, false)]
        [TestCase("Ann & Pat Smith", "Pat Smith", true, false)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", false, true)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", true, true)]
        public void complete_canonical_credit_should_remain_author_evidence(string canonicalName, string releaseAuthor, bool structured, bool hasAuthorEvidence)
        {
            var identity = Analyze(releaseAuthor + " - A Book Title", canonicalName, "A Book Title",
                null, null, structuredAuthor: structured ? releaseAuthor : string.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(hasAuthorEvidence));
                Assert.That(identity.HasStructuredAuthorMismatch, Is.EqualTo(structured && !hasAuthorEvidence));
            });
        }

        [TestCase("Louise Penny - [Inspector Rebus 16] - All the Devils Are Here")]
        public void should_treat_unrecognized_series_text_as_neutral_when_author_and_edition_title_are_exact(string releaseTitle)
        {
            var identity = Analyze(
                releaseTitle,
                "Louise Penny",
                "All the Devils are Here",
                "Chief Inspector Armand Gamache",
                "16");

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
        }

        [TestCase("Pat Smith", "Titre par Pat Smith", true)]
        [TestCase("Pat Smith", "Gelesen von Pat Smith", true)]
        [TestCase("Pat Smith", "Ann und Pat Smith", true)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer Q.", false)]
        [TestCase("Jennifer Q. Armentrout", "Armentrout, Jennifer L.", false)]
        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("Ann Smith", "Mary Ann Smith", true)]
        [TestCase("Lee", "Stan Lee", true)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith", false)]
        [TestCase("John Smith", "John Adams and Mary Smith", false)]
        [TestCase("Brian Herbert", "Kevin J Anderson, Brian Herbert", true)]
        [TestCase("Robert Stevenson", "Robert Louis Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Robert Q. Stevenson", false)]
        public void structured_release_author_should_require_a_complete_compatible_credit(string authorName, string suppliedCredit, bool matches)
        {
            var author = new Author { Id = 38, Name = authorName };
            var book = new Book
            {
                Id = 501, AuthorId = author.Id, Author = author, Title = "A Book Title",
                Editions = new List<Edition> { new() { Title = "A Book Title", Monitored = true } }
            };
            var release = new ReleaseInfo { Title = book.Title, Book = book.Title, Author = suppliedCredit };

            var titleMatch = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { book }, new[] { book });
            Assert.That(titleMatch, Is.Not.Null);
            var identity = ReleaseIdentityEvidence.Analyze(release, author, titleMatch);

            Assert.Multiple(() =>
            {
                Assert.That(titleMatch.IsMatch, Is.EqualTo(matches));
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(matches));
                Assert.That(identity.HasStructuredAuthorMismatch, Is.EqualTo(!matches));
            });
        }

        [TestCase("Ann Smith", "Mary Ann Smith - A Book Title", true)]
        [TestCase("Lee", "Stan Lee - A Book Title", true)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith - A Book Title", false)]
        [TestCase("John Smith", "John Adams and Mary Smith - A Book Title", false)]
        [TestCase("Ann Smith", "Ann Smith A Book Title", true)]
        [TestCase("Ann Smith", "A Book Title Ann Smith", true)]
        [TestCase("Ann Smith", "Ann Smith - A Book Title", true)]
        [TestCase("Ann Smith", "Ann Smith John Smith - A Book Title", true)]
        [TestCase("Brian Herbert", "Kevin J Anderson, Brian Herbert - A Book Title", true)]
        [TestCase("Robert Stevenson", "Robert Louis Stevenson A Book Title", true)]
        [TestCase("Robert Louis Stevenson", "Robert Q. Stevenson - A Book Title", false)]
        public void raw_release_author_should_preserve_complete_credit_and_matched_title_boundaries(string authorName, string releaseTitle, bool matches)
        {
            var author = new Author { Id = 38, Name = authorName };
            var book = new Book
            {
                Id = 501, AuthorId = author.Id, Author = author, Title = "A Book Title",
                Editions = new List<Edition> { new() { Title = "A Book Title", Monitored = true } }
            };
            var release = new ReleaseInfo { Title = releaseTitle };

            var titleMatch = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { book }, new[] { book });
            Assert.That(titleMatch, Is.Not.Null);
            var identity = ReleaseIdentityEvidence.Analyze(release, author, titleMatch);

            Assert.Multiple(() =>
            {
                Assert.That(titleMatch.IsMatch, Is.EqualTo(matches));
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(matches));
                Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            });
        }

        [TestCase("")]
        [TestCase("Gabriel Jose de la Concordia Garcia Marquez")]
        public void long_compatible_credit_should_supply_raw_and_structured_author_evidence(string structuredAuthor)
        {
            var identity = Analyze("Gabriel Jose de la Concordia Garcia Marquez - One Hundred Years of Solitude",
                "Gabriel Garcia Marquez", "One Hundred Years of Solitude", null, null, structuredAuthor: structuredAuthor);

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
                Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            });
        }

        [TestCase("Pat Smith", "Ann and Pat Smith", "", true)]
        [TestCase("Pat Smith", "Ann and Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", "", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", "Ann und Pat Smith", true)]
        [TestCase("Ann Smith", "Ann and Pat Smith", "", true)]
        [TestCase("Ann Smith", "Ann and Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", "", false)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", "Ann und Pat Smith", false)]
        [TestCase("Ann and Pat Smith", "Ann and Pat Smith", "", true)]
        [TestCase("Ann and Pat Smith", "Ann and Pat Smith", "Ann and Pat Smith", true)]
        public void author_evidence_should_compare_supplied_words_without_a_language_specific_separator(string authorName, string releaseAuthor, string structuredAuthor, bool hasAuthorEvidence)
        {
            var identity = Analyze(releaseAuthor + " - A Book Title", authorName, "A Book Title", null, null, structuredAuthor: structuredAuthor);

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(hasAuthorEvidence));
                Assert.That(identity.HasStructuredAuthorMismatch, Is.EqualTo(!hasAuthorEvidence && structuredAuthor.Length > 0));
            });
        }

        [TestCase("John Smith", "John Smith", 0, 1, false)]
        [TestCase("John Smith John Smith", "John Smith", 2, 3, true)]
        [TestCase("Smith John Additional Title Words", "Additional Title Words", 2, 4, true)]
        [TestCase("Smith John Additional Title Words", "Title Words", 3, 4, true)]
        public void retained_author_spans_should_preserve_title_overlap_and_inverted_title_boundaries(string releaseTitle, string bookTitle, int titleStart, int titleEnd, bool hasAuthorEvidence)
        {
            var author = new Author { Name = "John Smith" };
            var identity = ReleaseIdentityEvidence.Analyze(new ReleaseInfo { Title = releaseTitle }, author,
                new TitleMatchResult
                {
                    MatchedVariant = bookTitle,
                    MatchedStart = titleStart,
                    MatchedEnd = titleEnd
                });

            Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(hasAuthorEvidence));
        }

        [TestCase("Viserion-Fire and Blood-EP-WEB-FLAC-2026-ENTiTLED", "George R.R. Martin", "Fire & Blood")]
        [TestCase("Normal People - Paragon - WEB - 2014", "Sally Rooney", "Normal People")]
        public void should_not_invent_a_creator_mismatch_from_unstructured_residue(string releaseTitle, string authorName, string editionTitle)
        {
            var identity = Analyze(releaseTitle, authorName, editionTitle, null, null);

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasAuthorIdentityEvidence, Is.False);
        }

        [TestCase("John Smith", true)]
        [TestCase("Smith, John", true)]
        [TestCase("J. Smith", false)]
        [TestCase("John A. Smith", false)]
        [TestCase("John Smyth", false)]
        [TestCase("Narrated by John Smith", true)]
        [TestCase("Read by John Smith", true)]
        [TestCase("Performed by John Smith", true)]
        [TestCase("by John Smith", true)]
        [TestCase("NARRATED BY John Smith", true)]
        [TestCase("Narrated by John Smith, Part 2 of 2", true)]
        [TestCase("Narrated by John Smith and the Other Story", true)]
        [TestCase("Lu par John Smith", true)]
        [TestCase("Gelesen von John Smith", true)]
        [TestCase("credit John Smith additional words", true)]
        [TestCase("John; Smith", false)]
        [TestCase("John； Smith", false)]
        [TestCase("Narrated by John; Smith", false)]
        [TestCase("Read by John; Smith", false)]
        public void known_narrator_qualifier_must_preserve_contributor_boundaries(string qualifier, bool canOmit)
        {
            var book = CreateNarratorBook("A Book Title [" + qualifier + "]");
            var shortened = ReleaseTitleMatchScorer.FindBestMatch(
                "Jane Doe - A Book Title", book.Author.Name, new[] { book });
            var complete = ReleaseTitleMatchScorer.FindBestMatch(
                "Jane Doe - " + book.Title, book.Author.Name, new[] { book });

            TestContext.Progress.WriteLine($"Narrator qualifier: qualifier='{qualifier}', shortened={shortened?.IsMatch == true}, complete={complete?.IsMatch == true}.");
            Assert.Multiple(() =>
            {
                Assert.That(shortened?.IsMatch == true, Is.EqualTo(canOmit));
                Assert.That(complete?.IsMatch == true, Is.True);
            });
        }

        [TestCase("John Smith - A Book Title - Jane Doe", true)]
        [TestCase("Jane Doe - A Book Title John Smith", true)]
        [TestCase("John Smith - A Book Title", false)]
        [TestCase("A Book Title John Smith", false)]
        [TestCase("John; Smith - A Book Title", false)]
        [TestCase("A Book Title - John; Smith", false)]
        [TestCase("John; Smith A Book Title", false)]
        [TestCase("A Book Title John; Smith", false)]
        [TestCase("Read by John; Smith - A Book Title", false)]
        public void known_narrator_cannot_replace_missing_author(string releaseTitle, bool accepted)
        {
            var book = CreateNarratorBook("A Book Title");
            var release = new ReleaseInfo { Title = releaseTitle };
            var criteria = new BookSearchCriteria
            {
                Author = book.Author,
                Books = new List<Book> { book },
                AuthorCatalog = new List<Book> { book }
            };
            var spec = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(new RemoteBook { Release = release }, criteria);
            var match = ReleaseTitleMatchScorer.FindBestMatch(release, book.Author.Name, new[] { book }, new[] { book });
            var identity = ReleaseIdentityEvidence.Analyze(release, book.Author, match);

            TestContext.Progress.WriteLine($"Narrator boundary: release='{releaseTitle}', scorer={match?.IsMatch == true}, authorEvidence={identity.HasAuthorIdentityEvidence}, accepted={decision.Accepted}.");
            Assert.That(decision.Accepted, Is.EqualTo(accepted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void narrator_qualifier_must_match_a_complete_stored_credit(bool combinedStoredCredit)
        {
            var book = CreateNarratorBook("A Book Title [John Smith; Jane Doe]");
            book.Editions[0].NarratorNames = combinedStoredCredit
                ? new List<string> { "John Smith; Jane Doe" }
                : new List<string> { "John Smith", "Jane Doe" };

            var match = ReleaseTitleMatchScorer.FindBestMatch(
                "Jane Doe - A Book Title", book.Author.Name, new[] { book });

            Assert.That(match?.IsMatch == true, Is.True);
        }

        private static Book CreateNarratorBook(string title)
        {
            var author = new Author { Id = 37, Name = "Jane Doe" };
            return new Book
            {
                Id = 501,
                AuthorId = author.Id,
                Author = author,
                Title = title,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { Id = 1, Title = title, Monitored = true, NarratorNames = new List<string> { "John Smith" } }
                }
            };
        }

        private static ReleaseIdentityEvidence Analyze(string releaseTitle, string authorName, string editionTitle, string seriesName, string seriesPosition, IEnumerable<string> authorPseudonyms = null, string structuredAuthor = "")
        {
            var author = new Author
            {
                Name = authorName,
                Pseudonyms = authorPseudonyms?.ToList() ?? new List<string>()
            };
            var book = new Book
            {
                Author = author,
                Title = editionTitle,
                SeriesName = seriesName,
                SeriesPosition = seriesPosition,
                Editions = new List<Edition>
                {
                    new Edition { Title = editionTitle, Monitored = true }
                }
            };
            var release = new ReleaseInfo
            {
                Title = releaseTitle,
                Author = structuredAuthor
            };

            var match = ReleaseTitleMatchScorer.FindBestMatch(
                release.Title,
                author.Name,
                new[] { book },
                null,
                new[] { book });

            Assert.That(match, Is.Not.Null);

            return ReleaseIdentityEvidence.Analyze(release, author, match);
        }
    }
}
