using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    /// <summary>
    /// The automatic (completed download) import path used to block files that the manual import
    /// preview resolves instantly, because the download context disabled path/filename evidence
    /// while the preview context allows it. These tests pin both halves of that difference.
    /// </summary>
    [TestFixture]
    public class AutoImportLocalPreviewParityFixture
    {
        private const string GalileoFilePath =
            "/downloads/completed/audio-book/Paul Strathern - Galileo_And_The_Solar_System (2013)/Paul Strathern - Galileo_And_The_Solar_System (2013).mp3";

        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null) { }
            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null) { }
            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }
            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null) { }
            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new List<MatchingLogEntry>();
            public void ClearLogs() { }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => _author;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => new List<Author> { _author };
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => new List<Author> { _author };
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => new Dictionary<int, string>();
            public bool AuthorPathExists(string folder) => false;
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() { }
        }

        /// <summary>
        /// Only answers when the query carries the book's title tokens. The download folder spells the
        /// title with underscores, so a hit here also proves separator normalisation is shared.
        /// </summary>
        private sealed class GalileoEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<List<string>> Calls = new();

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Select(token => token.ToLowerInvariant())
                    .ToList();
                Calls.Add(tokenList);

                if (!tokenList.Contains("galileo") || !tokenList.Contains("solar") || !tokenList.Contains("system"))
                {
                    return new List<EditionFtsMatch>();
                }

                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 9101,
                        BookId = 5150,
                        EditionTitle = "Galileo and the Solar System",
                        BookTitle = "Galileo and the Solar System",
                        AuthorId = 4242,
                        AuthorName = "Paul Strathern",
                        ReadingFormatId = 2,
                        MatchScore = 14
                    }
                };
            }
        }

        /// <summary>
        /// Two different local books answer the same path tokens. Path evidence must not be allowed to
        /// pick a winner here.
        /// </summary>
        private sealed class AmbiguousEditionFtsRepository : IEditionFtsRepository
        {
            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Select(token => token.ToLowerInvariant())
                    .ToList();

                if (!tokenList.Contains("galileo"))
                {
                    return new List<EditionFtsMatch>();
                }

                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 9101,
                        BookId = 5150,
                        EditionTitle = "Galileo",
                        BookTitle = "Galileo",
                        AuthorId = 4242,
                        AuthorName = "Paul Strathern",
                        ReadingFormatId = 2,
                        MatchScore = 12
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 9102,
                        BookId = 5151,
                        EditionTitle = "Galileo",
                        BookTitle = "Galileo",
                        AuthorId = 7777,
                        AuthorName = "John Heilbron",
                        ReadingFormatId = 2,
                        MatchScore = 12
                    }
                };
            }
        }

        private static FileMatchingService BuildService(IEditionFtsRepository fts, Author author)
        {
            var logger = LogManager.GetCurrentClassLogger();

            return new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
        }

        private static DiscoveredFileWithMetadata GalileoFile()
        {
            // The live case: an mp3 whose embedded tags carry nothing the library can be searched by.
            return new DiscoveredFileWithMetadata
            {
                Path = GalileoFilePath,
                Size = 42,
                Modified = DateTime.UtcNow,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };
        }

        [Test]
        public async Task strict_downloaded_context_reproduces_the_blocked_queue_item()
        {
            var fts = new GalileoEditionFtsRepository();
            var svc = BuildService(fts, new Author { Id = 4242, Name = "Paul Strathern" });

            var context = MatchingContextPresets.ForDownloaded(false, targetBookIds: null, allowPathFallback: false);
            var result = await svc.MatchFilesToLibraryAsync(new[] { GalileoFile() }, restrictToAuthorId: null, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.Calls.Any(call => call.Contains("galileo")), Is.False,
                "the strict download context must never reach the library with path-derived tokens");
        }

        [Test]
        public async Task local_preview_parity_context_matches_the_underscored_download_folder()
        {
            var fts = new GalileoEditionFtsRepository();
            var svc = BuildService(fts, new Author { Id = 4242, Name = "Paul Strathern" });

            // Exactly the context the automatic path's second pass now uses.
            var context = MatchingContextPresets.ForDownloaded(false, targetBookIds: null, allowPathFallback: true);
            var result = await svc.MatchFilesToLibraryAsync(new[] { GalileoFile() }, restrictToAuthorId: null, context);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].BookId, Is.EqualTo(5150));
            Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(9101));
            Assert.That(fts.Calls.Any(call => call.Contains("galileo") && call.Contains("solar") && call.Contains("system")), Is.True,
                "underscores in the download folder name must be split into separate tokens");
        }

        [Test]
        public async Task manual_preview_context_matches_the_same_file_as_the_parity_context()
        {
            var fts = new GalileoEditionFtsRepository();
            var svc = BuildService(fts, new Author { Id = 4242, Name = "Paul Strathern" });

            var result = await svc.MatchFilesToLibraryAsync(new[] { GalileoFile() }, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(9101));
        }

        [Test]
        public async Task near_miss_folder_name_must_not_match_even_with_path_evidence()
        {
            var fts = new GalileoEditionFtsRepository();
            var svc = BuildService(fts, new Author { Id = 4242, Name = "Paul Strathern" });

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/completed/audio-book/Paul Strathern - Newton_And_Gravity (2013)/Paul Strathern - Newton_And_Gravity (2013).mp3",
                Size = 42,
                Modified = DateTime.UtcNow,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };

            var context = MatchingContextPresets.ForDownloaded(false, targetBookIds: null, allowPathFallback: true);
            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: null, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
        }

        [Test]
        public async Task competing_same_title_candidates_must_be_decided_by_author_evidence()
        {
            var svc = BuildService(new AmbiguousEditionFtsRepository(), new Author { Id = 4242, Name = "Paul Strathern" });

            var context = MatchingContextPresets.ForDownloaded(false, targetBookIds: null, allowPathFallback: true);
            var result = await svc.MatchFilesToLibraryAsync(new[] { GalileoFile() }, restrictToAuthorId: null, context);

            // Two identically-titled books by different authors: path evidence may only pick the one whose
            // author the path actually names, never the equally-scored twin.
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(9101));
            Assert.That(result.MatchedFiles[0].AuthorName, Is.EqualTo("Paul Strathern"));
        }
    }
}
