using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceEditionLanguageFilterFixture
    {
        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authors;

            public StubAuthorService(params Author[] authors)
            {
                _authors = authors.ToDictionary(a => a.Id);
            }

            public Author GetAuthor(int authorId)
            {
                return _authors.TryGetValue(authorId, out var author) ? author : null;
            }

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
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => new List<BookFile>();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => new List<BookFile>();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(IAuthorService authorService, IMediaFileService mediaFileService, Logger logger)
                : base(bookService: null,
                    authorService: authorService,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: null,
                    mediaFileService: mediaFileService,
                    historyService: null,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: new EditionSelector(logger),
                    editionMetadataProfileFilter: new NzbDrone.Core.Books.Services.EditionMetadataProfileFilter(new TestTermMatcherService()),
                    mediaCoverService: null,
                    logger: logger)
            {
            }

            public List<Edition> SelectRemoteEditions(Book local, Book remote)
            {
                return GetRemoteChildren(local, remote);
            }
        }

        [Test]
        public void should_filter_remote_editions_by_metadata_profile_language_even_when_book_author_has_no_profiles_loaded()
        {
            Assert.That("eng".CanonicalizeLanguage(), Is.EqualTo("eng"));
            Assert.That("deu".CanonicalizeLanguage(), Is.EqualTo("deu"));

            var profile = new MetadataProfile { AllowedLanguages = "eng" };
            var author = new Author
            {
                Id = 42,
                AudiobookMetadataProfile = profile
            };

            var service = new TestableRefreshBookService(new StubAuthorService(author), new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 1, MediaType = BookMediaType.Audiobook, Title = "Test Book" };
            local.AuthorId = 42; // Sets local.Author.Id, but profiles are not loaded on the stub Author object.

            Assert.That(local.AuthorId, Is.EqualTo(42));
            Assert.That(local.Author.Id, Is.EqualTo(42));

            var english = new Edition
            {
                ForeignEditionId = "e1",
                Title = "English Edition",
                Language = "eng",
                ReadingFormatId = 2
            };

            var german = new Edition
            {
                ForeignEditionId = "e2",
                Title = "German Edition",
                Language = "deu",
                ReadingFormatId = 2
            };

            var remote = new Book
            {
                Title = "Remote",
                Editions = new List<Edition> { english, german }
            };

            var selected = service.SelectRemoteEditions(local, remote);

            Assert.That(selected.Select(e => e.ForeignEditionId).ToList(), Is.EqualTo(new List<string> { "e1" }));
        }

        [Test]
        public void should_keep_one_ebook_representative_when_audiobook_language_has_no_audio_coverage()
        {
            var profile = new MetadataProfile { AllowedLanguages = "eng,fra" };
            var author = new Author
            {
                Id = 42,
                AudiobookMetadataProfile = profile
            };

            var service = new TestableRefreshBookService(new StubAuthorService(author), new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 1, MediaType = BookMediaType.Audiobook, Title = "Dune" };
            local.AuthorId = 42;

            var remote = new Book
            {
                Title = "Dune",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "audio-eng", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "ebook-eng", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "print-eng", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                    new Edition { ForeignEditionId = "ebook-fra", Title = "Dune French", Language = "fra", ReadingFormatId = 3 }
                }
            };

            var selected = service.SelectRemoteEditions(local, remote);

            Assert.That(selected.Select(e => e.ForeignEditionId).ToList(),
                Is.EqualTo(new List<string> { "audio-eng", "ebook-eng", "ebook-fra" }));
        }

        [Test]
        public void should_treat_null_language_as_allowed_bucket_for_audiobook_representative_refresh()
        {
            var profile = new MetadataProfile { AllowedLanguages = "null" };
            var author = new Author
            {
                Id = 42,
                AudiobookMetadataProfile = profile
            };

            var service = new TestableRefreshBookService(new StubAuthorService(author), new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 1, MediaType = BookMediaType.Audiobook, Title = "Dune" };
            local.AuthorId = 42;

            var remote = new Book
            {
                Title = "Dune",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "audio-null", Title = "Dune Audio", Language = null, ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "ebook-null", Title = "Dune", Language = null, ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "ebook-eng", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
                }
            };

            var selected = service.SelectRemoteEditions(local, remote);

            Assert.That(selected.Select(e => e.ForeignEditionId).ToList(),
                Is.EqualTo(new List<string> { "audio-null", "ebook-null" }));
        }

        [Test]
        public void should_filter_remote_editions_by_ignored_terms_during_book_refresh()
        {
            var profile = new MetadataProfile { Ignored = new List<string> { "illustrated" } };
            var author = new Author
            {
                Id = 42,
                EbookMetadataProfile = profile
            };

            var service = new TestableRefreshBookService(new StubAuthorService(author), new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 1, MediaType = BookMediaType.Ebook, Title = "Dune" };
            local.AuthorId = 42;

            var remote = new Book
            {
                Title = "Dune",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "ebook-good", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "ebook-illustrated", Title = "Dune (Illustrated Edition)", Language = "eng", ReadingFormatId = 3 }
                }
            };

            var selected = service.SelectRemoteEditions(local, remote);

            Assert.That(selected.Select(e => e.ForeignEditionId).ToList(),
                Is.EqualTo(new List<string> { "ebook-good" }));
        }
    }
}
