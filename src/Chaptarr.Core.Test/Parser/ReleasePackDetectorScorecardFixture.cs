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
    public class ReleasePackDetectorScorecardFixture
    {
        private const string ScorecardRelativePath = "src/Chaptarr.Core.Test/Parser/Fixtures/release-pack-scorecard.ndjson";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        [TestCaseSource(nameof(GetScorecardCases))]
        public void should_detect_release_pack_scorecard(ReleasePackScorecardRow row)
        {
            var author = new Author { Name = row.TargetAuthor };
            var targetBook = BuildBook(row, author);
            var authorCatalog = BuildAuthorCatalog(row, author, targetBook);

            var result = ReleasePackDetector.Detect(row.ReleaseTitle, targetBook, authorCatalog);
            var expectedVerdict = ParseVerdict(row);

            Assert.That(result.Verdict, Is.EqualTo(expectedVerdict),
                $"Case {row.CaseId}: release '{row.ReleaseTitle}' expected Verdict={expectedVerdict}, got Verdict={result.Verdict}. Type={result.PackType}; Match={result.MatchedValue}");
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

                ReleasePackScorecardRow row;
                try
                {
                    row = JsonSerializer.Deserialize<ReleasePackScorecardRow>(line, JsonOptions);
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
                    .SetName($"pack_scorecard_{row.CaseId}");
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

        private static Book BuildBook(ReleasePackScorecardRow row, Author author)
        {
            var editionTitle = string.IsNullOrWhiteSpace(row.TargetEditionTitle)
                ? row.TargetBookTitle
                : row.TargetEditionTitle;

            return new Book
            {
                Id = row.TargetBookId,
                Title = row.TargetBookTitle,
                MediaType = ParseMediaType(row.TargetMediaType),
                HardcoverBookId = row.TargetHardcoverWorkId,
                Author = author,
                SeriesName = row.TargetSeriesName,
                SeriesPosition = row.TargetSeriesPosition,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = row.TargetBookId,
                        Title = editionTitle,
                        Monitored = true
                    }
                }
            };
        }

        private static List<Book> BuildAuthorCatalog(ReleasePackScorecardRow row, Author author, Book targetBook)
        {
            var catalog = new List<Book> { targetBook };

            foreach (var catalogBook in row.AuthorCatalog ?? Enumerable.Empty<ReleasePackScorecardBookRow>())
            {
                catalog.Add(new Book
                {
                    Id = catalogBook.BookId,
                    Title = catalogBook.Title,
                    MediaType = ParseMediaType(catalogBook.MediaType),
                    HardcoverBookId = catalogBook.HardcoverWorkId,
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

        private static BookMediaType ParseMediaType(string mediaType)
        {
            if (Enum.TryParse<BookMediaType>(mediaType, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return BookMediaType.Audiobook;
        }

        private static ReleasePackDetectionVerdict ParseVerdict(ReleasePackScorecardRow row)
        {
            if (Enum.TryParse<ReleasePackDetectionVerdict>(row.ExpectedVerdict, ignoreCase: true, out var verdict))
            {
                return verdict;
            }

            throw new InvalidDataException($"Case {row.CaseId} has unknown expectedVerdict '{row.ExpectedVerdict}'");
        }

        public sealed class ReleasePackScorecardRow
        {
            public string CaseId { get; set; }
            public string Source { get; set; }
            public string Indexer { get; set; }
            public string Protocol { get; set; }
            public string ReleaseTitle { get; set; }
            public string ReleaseAuthor { get; set; }
            public string TargetAuthor { get; set; }
            public int TargetBookId { get; set; }
            public string TargetBookTitle { get; set; }
            public string TargetEditionTitle { get; set; }
            public string TargetMediaType { get; set; }
            public string TargetHardcoverWorkId { get; set; }
            public string TargetSeriesName { get; set; }
            public string TargetSeriesPosition { get; set; }
            public string ExpectedVerdict { get; set; }
            public string Notes { get; set; }
            public List<ReleasePackScorecardBookRow> AuthorCatalog { get; set; }
        }

        public sealed class ReleasePackScorecardBookRow
        {
            public int BookId { get; set; }
            public string Title { get; set; }
            public string EditionTitle { get; set; }
            public string MediaType { get; set; }
            public string HardcoverWorkId { get; set; }
            public string SeriesName { get; set; }
            public string SeriesPosition { get; set; }
        }
    }
}
