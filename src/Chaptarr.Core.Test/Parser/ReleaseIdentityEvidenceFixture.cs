using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseIdentityEvidenceFixture
    {
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
            Assert.That(identity.HasPositiveIdentityEvidence, Is.True);
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
            Assert.That(identity.HasPositiveIdentityEvidence, Is.True);
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
            Assert.That(identity.HasPositiveIdentityEvidence, Is.True);
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
            Assert.That(identity.HasPositiveIdentityEvidence, Is.True);
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
            Assert.That(identity.HasPositiveIdentityEvidence, Is.True);
        }

        [TestCase("Viserion-Fire and Blood-EP-WEB-FLAC-2026-ENTiTLED", "George R.R. Martin", "Fire & Blood")]
        [TestCase("Normal People - Paragon - WEB - 2014", "Sally Rooney", "Normal People")]
        public void should_not_invent_a_creator_mismatch_from_unstructured_residue(string releaseTitle, string authorName, string editionTitle)
        {
            var identity = Analyze(releaseTitle, authorName, editionTitle, null, null);

            Assert.That(identity.HasStructuredAuthorMismatch, Is.False);
            Assert.That(identity.HasPositiveIdentityEvidence, Is.False);
        }

        private static ReleaseIdentityEvidence Analyze(string releaseTitle, string authorName, string editionTitle, string seriesName, string seriesPosition, IEnumerable<string> authorAliases = null, string structuredAuthor = "")
        {
            var author = new Author
            {
                Name = authorName,
                Aliases = authorAliases?.ToList() ?? new List<string>()
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

            return ReleaseIdentityEvidence.Analyze(release, author, book, match);
        }
    }
}
