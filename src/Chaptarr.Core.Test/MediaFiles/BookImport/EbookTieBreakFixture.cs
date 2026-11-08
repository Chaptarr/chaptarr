using Chaptarr.Core.Test.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class EbookTieBreakFixture
    {
        [Test]
        public void equally_matching_editions_prefer_monitored_regardless_of_row_or_order(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values(false, true)] bool clonedBook,
            [Values(false, true)] bool monitoredFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author { Name = "Louisa May Alcott", CleanName = "louisamayalcott",
                    SortName = "louisamayalcott", TitleSlug = "louisa-may-alcott", Path = "/library/Alcott", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var candidates = new List<Edition>();
                Book book = null;
                for (var index = 0; index < 2; index++)
                {
                    if (book == null || clonedBook)
                    {
                        book = books.Insert(new Book { AuthorId = author.Id, Title = "The Annotated Little Women",
                            CleanTitle = "the annotated little women", TitleSlug = "little-women-" + index,
                            MediaType = mediaType, BaseBookId = "gr:little-women", Added = DateTime.UtcNow, AnyEditionOk = true });
                    }

                    candidates.Add(editions.Insert(new Edition { BookId = book.Id, Title = book.Title,
                        MatchingTitle = book.CleanTitle, ForeignEditionId = "gr:constructed-little-women-" + index,
                        Publisher = "W. W. Norton & Company", ReleaseDate = new DateTime(2015, 1, 1), Language = "eng",
                        ReadingFormatId = mediaType == BookMediaType.Ebook ? 3 : 2,
                        DurationSeconds = mediaType == BookMediaType.Audiobook ? 36000 : null,
                        NarratorNames = mediaType == BookMediaType.Audiobook ? new() { "Alice Reader" } : new(),
                        Monitored = (index == 0) == monitoredFirst }));
                }

                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var authorService = new AuthorService(authors, events, null, null, null, new CacheManager(), books,
                    null, logger, null, null, null, database, aliases);
                var editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, authorService, null, null,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var fts = new EditionFtsRepository(database, logger);
                fts.RebuildIndex();
                var matcher = new FileMatchingService(null, null, new ContainmentValidator(new TagNormalizer(), logger),
                    null, null, null, null, ConfigServiceTestProxy.Create(usePathAsTagsFallback: false), authorService,
                    events, null, fts, bookService, editionService, editions, null, logger);
                var file = new DiscoveredFileWithMetadata
                {
                    Path = "/matching-tie/book." + (mediaType == BookMediaType.Ebook ? "epub" : "m4b"),
                    DurationSeconds = mediaType == BookMediaType.Audiobook ? 36000 : null,
                    AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TITLE"] = new() { book.Title }, ["ARTIST"] = new() { author.Name },
                        ["PUBLISHER"] = new() { "W. W. Norton & Company" }, ["YEAR"] = new() { "2015" }
                    }
                };
                if (mediaType == BookMediaType.Audiobook) file.AllTags["NARRATOR"] = new() { "Alice Reader" };

                var match = matcher.HolyGrailMatchFile(file, mediaType, author.Id);
                Assert.That(match?.EditionId, Is.EqualTo(candidates.Single(edition => edition.Monitored).Id));
            });
        }

        private sealed class StubEditionFtsRepository : StagedEditionFtsStub
        {
            private readonly List<EditionFtsMatch> _results;

            public StubEditionFtsRepository(List<EditionFtsMatch> results)
            {
                _results = results ?? new List<EditionFtsMatch>();
            }

            public override bool FtsTableExists() => true;
            public override void RebuildIndex() { }

            protected override List<EditionFtsMatch> SelectEditions(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit)
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
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public void SetTags(Author author, HashSet<int> tags) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> author, bool useExistingRelativePath) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void UpdateMany(List<Author> authors) => throw new NotImplementedException();
            public bool AuthorPathIsValid(Author author) => throw new NotImplementedException();
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
                new EditionFtsMatch { EditionId = 1, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Penguin", ReleaseDate = new DateTime(2015, 1, 1), Monitored = true },
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
                new EditionFtsMatch { EditionId = 1, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = null, ReleaseDate = new DateTime(2010, 1, 1), Monitored = true },
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
                new EditionFtsMatch { EditionId = 2, BookId = 1, EditionTitle = "Some Book", BookTitle = "Some Book", AuthorId = 25, AuthorName = "Test Author", Publisher = "Pottermore Publishing", ReleaseDate = new DateTime(2012, 1, 1), ReadingFormatId = 3, MatchScore = 9.0, Monitored = true }
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
