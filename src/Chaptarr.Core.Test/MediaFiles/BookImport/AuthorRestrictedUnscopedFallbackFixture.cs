using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorRestrictedUnscopedFallbackFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
            {
                return new List<MatchingLogEntry>();
            }

            public void ClearLogs()
            {
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => _author;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new System.NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new System.NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new System.NotImplementedException();
            public Author FindByName(string title) => throw new System.NotImplementedException();
            public Author FindByNameInexact(string title) => throw new System.NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new System.NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new System.NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new System.NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new System.NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new System.NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new System.NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new System.NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new System.NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new System.NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new System.NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new System.NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new System.NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new System.NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new System.NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new System.NotImplementedException();
        }

        private sealed class BranchingEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<int?> AuthorIdCalls = new List<int?>();

            public bool FtsTableExists() => true;
            public void RebuildIndex()
            {
            }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                AuthorIdCalls.Add(authorId);

                // Author-restricted search yields no results (simulates wrong/mis-scoped author).
                if (authorId.HasValue)
                {
                    return new List<EditionFtsMatch>();
                }

                // Unscoped search yields a valid match.
                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 675,
                        BookId = 224,
                        EditionTitle = "Whipping Star",
                        BookTitle = "Whipping Star",
                        AuthorId = 6,
                        AuthorName = "Frank Herbert",
                        NarratorNames = "Scott Brick"
                    }
                };
            }

        }

        [Test]
        public async Task should_unscope_when_scoped_author_not_in_tags()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var authorService = new StubAuthorService(new Author
            {
                Id = 31,
                Name = "Brian Herbert",
                Path = "/audiobooks/Brian Herbert"
            });

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star - Scott Brick/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Whipping Star" } },
                    { "ARTIST", new List<string> { "Frank Herbert" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: 31, forDownloads: false);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].AuthorName, Is.EqualTo("Frank Herbert"));
            Assert.That(result.MatchedFiles[0].BookTitle, Is.EqualTo("Whipping Star"));

            // The mis-scoped author should be skipped and only unscoped FTS should run.
            Assert.That(fts.AuthorIdCalls.All(id => id == null), Is.True);
        }
    }
}
