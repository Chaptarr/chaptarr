using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleMatchScorerScorecardFixture
    {
        private const string ScorecardRelativePath = "src/Chaptarr.Core.Test/Parser/Fixtures/release-title-scorecard.ndjson";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        [TestCaseSource(nameof(GetScorecardCases))]
        public void should_match_release_title_scorecard(ReleaseTitleScorecardRow row)
        {
            var author = new Author { Name = row.TargetAuthor };
            var targetBook = BuildBook(row, author);
            var authorCatalog = BuildAuthorCatalog(row, author, targetBook);

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                row.ReleaseTitle,
                row.TargetAuthor,
                new[] { targetBook },
                row.ReleaseAuthor,
                authorCatalog);

            var actualIsMatch = result?.IsMatch == true;
            var actualProblemCode = result?.ProblemCode ?? TitleMatchProblemCode.NoCandidate;
            var expectedProblemCode = ParseProblemCode(row);

            Assert.Multiple(() =>
            {
                Assert.That(actualIsMatch, Is.EqualTo(row.ExpectedIsMatch),
                    $"Case {row.CaseId}: release '{row.ReleaseTitle}' expected IsMatch={row.ExpectedIsMatch}, got IsMatch={actualIsMatch}. Leftovers=[{string.Join(", ", result?.MeaningfulLeftovers ?? new List<string>())}]");

                Assert.That(actualProblemCode, Is.EqualTo(expectedProblemCode),
                    $"Case {row.CaseId}: release '{row.ReleaseTitle}' expected ProblemCode={expectedProblemCode}, got ProblemCode={actualProblemCode}. Leftovers=[{string.Join(", ", result?.MeaningfulLeftovers ?? new List<string>())}]");
            });
        }

        private static IEnumerable<TestCaseData> GetScorecardCases()
        {
            var path = FindScorecardPath();
            var lineNumber = 0;

            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ReleaseTitleScorecardRow row;
                try
                {
                    row = JsonSerializer.Deserialize<ReleaseTitleScorecardRow>(line, JsonOptions);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Could not parse {ScorecardRelativePath}:{lineNumber}: {ex.Message}", ex);
                }

                if (row == null)
                {
                    throw new InvalidDataException($"Could not parse {ScorecardRelativePath}:{lineNumber}: row was null");
                }

                yield return new TestCaseData(row)
                    .SetName($"scorecard_{row.CaseId}");
            }
        }

        private static string FindScorecardPath()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, ScorecardRelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not find {ScorecardRelativePath} from {TestContext.CurrentContext.TestDirectory}");
        }

        private static Book BuildBook(ReleaseTitleScorecardRow row, Author author)
        {
            var editionTitle = string.IsNullOrWhiteSpace(row.TargetEditionTitle)
                ? row.TargetBookTitle
                : row.TargetEditionTitle;

            return new Book
            {
                Id = row.TargetBookId,
                Title = row.TargetBookTitle,
                Author = author,
                SeriesName = row.TargetSeriesName,
                SeriesPosition = row.TargetSeriesPosition,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = editionTitle,
                        Monitored = true
                    }
                }
            };
        }

        private static List<Book> BuildAuthorCatalog(ReleaseTitleScorecardRow row, Author author, Book targetBook)
        {
            var catalog = new List<Book> { targetBook };

            foreach (var catalogBook in row.AuthorCatalog ?? Enumerable.Empty<ReleaseTitleScorecardBookRow>())
            {
                catalog.Add(new Book
                {
                    Id = catalogBook.BookId,
                    Title = catalogBook.Title,
                    Author = author,
                    SeriesName = catalogBook.SeriesName,
                    SeriesPosition = catalogBook.SeriesPosition,
                    Editions = new List<Edition>
                    {
                        new Edition
                        {
                            Id = catalogBook.BookId,
                            Title = string.IsNullOrWhiteSpace(catalogBook.EditionTitle) ? catalogBook.Title : catalogBook.EditionTitle,
                            Monitored = true
                        }
                    }
                });
            }

            return catalog;
        }

        private static TitleMatchProblemCode ParseProblemCode(ReleaseTitleScorecardRow row)
        {
            if (Enum.TryParse<TitleMatchProblemCode>(row.ExpectedProblemCode, ignoreCase: true, out var code))
            {
                return code;
            }

            throw new InvalidDataException($"Case {row.CaseId} has unknown expectedProblemCode '{row.ExpectedProblemCode}'");
        }

        public sealed class ReleaseTitleScorecardRow
        {
            public string CaseId { get; set; }
            public string Source { get; set; }
            public string Indexer { get; set; }
            public string Protocol { get; set; }
            public string ReleaseTitle { get; set; }
            public string ReleaseAuthor { get; set; }
            public string ReleaseNarrator { get; set; }
            public string ReleaseQuality { get; set; }
            public string ReleaseLanguage { get; set; }
            public string TargetAuthor { get; set; }
            public int TargetBookId { get; set; }
            public string TargetBookTitle { get; set; }
            public string TargetEditionTitle { get; set; }
            public string TargetSeriesName { get; set; }
            public string TargetSeriesPosition { get; set; }
            public bool ExpectedIsMatch { get; set; }
            public string ExpectedProblemCode { get; set; }
            public string Notes { get; set; }
            public List<ReleaseTitleScorecardBookRow> AuthorCatalog { get; set; }
        }

        public sealed class ReleaseTitleScorecardBookRow
        {
            public int BookId { get; set; }
            public string Title { get; set; }
            public string EditionTitle { get; set; }
            public string SeriesName { get; set; }
            public string SeriesPosition { get; set; }
        }
    }
}
