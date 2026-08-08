using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class EbookTieBreakFixture
    {
        private sealed class StubEditionFtsRepository : IEditionFtsRepository
        {
            private readonly List<EditionFtsMatch> _results;

            public StubEditionFtsRepository(List<EditionFtsMatch> results)
            {
                _results = results ?? new List<EditionFtsMatch>();
            }

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                return _results;
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
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public void SetTags(Author author, HashSet<int> tags) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> author, bool useExistingRelativePath) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void UpdateMany(List<Author> authors) => throw new NotImplementedException();
            public bool AuthorPathIsValid(Author author) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public Author AddWantedEdition(int authorId, int editionId) => throw new NotImplementedException();
            public void SetMonitored(int authorId, bool monitored) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private static FileMatchingService CreateSut(List<EditionFtsMatch> candidates)
        {
            return new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 25, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(candidates),
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_prefer_publisher_match_when_available()
        {
            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch { EditionId = 1, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Penguin", ReleaseDate = new DateTime(2015, 1, 1) },
                new EditionFtsMatch { EditionId = 2, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Pottermore Publishing", ReleaseDate = new DateTime(2012, 1, 1) }
            };

            var sut = CreateSut(candidates);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/ebooks/Test Author/Some Book/Some Book.epub",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Some Book" } },
                    { "PUBLISHER", new List<string> { "Pottermore Publishing" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(2));
        }

        [Test]
        public void should_prefer_closest_year_when_publisher_is_unknown()
        {
            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch { EditionId = 1, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = null, ReleaseDate = new DateTime(2010, 1, 1) },
                new EditionFtsMatch { EditionId = 2, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = null, ReleaseDate = new DateTime(2019, 1, 1) }
            };

            var sut = CreateSut(candidates);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/ebooks/Test Author/Some Book/Some Book.epub",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Some Book" } },
                    { "YEAR", new List<string> { "2020" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(2));
        }

        [Test]
        public void should_prefer_closest_year_over_publisher_match_when_both_are_available()
        {
            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch { EditionId = 1, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Unknown Press", ReleaseDate = new DateTime(2020, 1, 1), ReadingFormatId = 3, MatchScore = 8.0 },
                new EditionFtsMatch { EditionId = 2, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Pottermore Publishing", ReleaseDate = new DateTime(2012, 1, 1), ReadingFormatId = 3, MatchScore = 9.0 }
            };

            var sut = CreateSut(candidates);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/ebooks/Test Author/Some Book/Some Book.epub",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Some Book" } },
                    { "PUBLISHER", new List<string> { "Pottermore Publishing" } },
                    { "YEAR", new List<string> { "2020" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1));
        }
    }
}
