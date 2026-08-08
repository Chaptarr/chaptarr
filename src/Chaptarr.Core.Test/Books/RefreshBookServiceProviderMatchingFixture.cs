using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceProviderMatchingFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly Dictionary<int, List<BookFile>> _filesByBookId = new();

            public void SetFilesByBook(int bookId, params BookFile[] files)
            {
                _filesByBookId[bookId] = files?.ToList() ?? new List<BookFile>();
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => _filesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
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

        private sealed class StubBookInfo : IProvideBookInfo
        {
            private readonly Tuple<string, Book, List<Author>> _bookInfo;
            public int GetWorkInfoCalls { get; private set; }

            public StubBookInfo(Tuple<string, Book, List<Author>> bookInfo)
            {
                _bookInfo = bookInfo;
            }

            public Tuple<string, Book, List<Author>> GetBookInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null) => _bookInfo;
            public Tuple<string, Book, List<Author>> GetWorkInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null)
            {
                GetWorkInfoCalls++;
                return _bookInfo;
            }

            public Tuple<string, Book, List<Author>> GetEditionInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook) => _bookInfo;
        }

        private sealed class StubAuthorInfo : IProvideAuthorInfo
        {
            private readonly Author _author;

            public StubAuthorInfo(Author author)
            {
                _author = author;
            }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true) => _author;
            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(
                IMediaFileService mediaFileService,
                Logger logger,
                IProvideAuthorInfo authorInfo = null,
                IProvideBookInfo bookInfo = null)
                : base(bookService: null,
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: authorInfo,
                    bookInfo: bookInfo,
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

            public Book MatchRemote(Book local, List<Book> remote)
            {
                return GetRemoteData(local, remote, null).Entity;
            }

            public bool ShouldDeletePublic(Book local)
            {
                return ShouldDelete(local);
            }
        }

        [Test]
        public void should_match_remote_book_by_stable_work_provider_id()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123"
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc-ed:123", Title = "Remote Audio", ReadingFormatId = 2 }
                }
            };

            var match = service.MatchRemote(local, new List<Book> { remote });

            Assert.That(match, Is.SameAs(remote));
        }

        [Test]
        public void should_prefer_matching_media_type_when_multiple_remote_books_share_work_provider_id()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123"
            };

            var remoteEbook = new Book
            {
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc-ed:ebook-123", Title = "Remote Ebook", ReadingFormatId = 1 }
                }
            };

            var remoteAudiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc-ed:audio-123", Title = "Remote Audio", ReadingFormatId = 2 }
                }
            };

            var match = service.MatchRemote(local, new List<Book> { remoteEbook, remoteAudiobook });

            Assert.That(match, Is.SameAs(remoteAudiobook));
        }

        [Test]
        public void should_not_match_by_edition_alias_when_remote_has_stable_work_id()
        {
            // Local book was imported when only Amazon IDs existed.
            var local = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                ASIN = "B00ABC1234"
            };

            // Remote book later gained Hardcover IDs and now uses hc:* as the primary id/base_book_id,
            // but still includes the Amazon provider id in its provider-id set.
            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:999",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc-ed:999", Title = "Remote Audio", ReadingFormatId = 2 }
                },
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "hc:999",
                    "az:B00ABC1234"
                }
            };

            var match = BookIdentity.FindWorkFirstMatches(new List<Book> { remote }, local);

            Assert.That(match, Is.Empty);
        }

        [Test]
        public void should_not_fall_back_to_direct_lookup_when_author_scoped_remote_snapshot_is_empty()
        {
            var local = new Book
            {
                Id = 1,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123"
            };

            var lookedUpBook = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc-ed:1", Title = "Dune Audio", ReadingFormatId = 2 }
                }
            };

            var author = new Author
            {
                Id = 99,
                Name = "Frank Herbert",
                HardcoverAuthorId = "hc:999"
            };

            var bookInfo = new StubBookInfo(Tuple.Create("hc:999", lookedUpBook, new List<Author> { author }));
            var service = new TestableRefreshBookService(
                new StubMediaFileService(),
                LogManager.GetCurrentClassLogger(),
                new StubAuthorInfo(author),
                bookInfo);

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(bookInfo.GetWorkInfoCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_prune_fileless_automatic_book_when_author_scoped_snapshot_has_no_match()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                Title = "Filtered Omnibus",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Automatic }
            };

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(service.ShouldDeletePublic(local), Is.True);
        }

        [Test]
        public void should_preserve_strict_edition_book_when_author_scoped_snapshot_has_no_match()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                Title = "Strict Edition",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AnyEditionOk = false,
                AddOptions = new AddBookOptions { AddType = BookAddType.Automatic }
            };

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(service.ShouldDeletePublic(local), Is.False);
        }

        [Test]
        public void should_preserve_manual_add_book_when_author_scoped_snapshot_has_no_match()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                Title = "Manual Book",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual }
            };

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(service.ShouldDeletePublic(local), Is.False);
        }

        [Test]
        public void should_preserve_manual_edition_book_when_author_scoped_snapshot_has_no_match()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                Title = "Manual Edition",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Automatic },
                Editions = new List<Edition>
                {
                    new Edition { Id = 10, BookId = 1, ManualAdd = true }
                }
            };

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(service.ShouldDeletePublic(local), Is.False);
        }

        [Test]
        public void should_preserve_file_backed_book_when_author_scoped_snapshot_has_no_match()
        {
            var mediaFileService = new StubMediaFileService();
            var service = new TestableRefreshBookService(mediaFileService, LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                Title = "Filed Book",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Automatic }
            };

            mediaFileService.SetFilesByBook(local.Id, new BookFile { Id = 10 });

            var match = service.MatchRemote(local, new List<Book>());

            Assert.That(match, Is.Null);
            Assert.That(service.ShouldDeletePublic(local), Is.False);
        }

        [Test]
        public void should_not_match_remote_book_when_metadata_profile_filters_out_all_editions()
        {
            var service = new TestableRefreshBookService(
                new StubMediaFileService(),
                LogManager.GetCurrentClassLogger(),
                bookInfo: new StubBookInfo(null));

            var local = new Book
            {
                Id = 1,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                Author = new Author
                {
                    Id = 42,
                    AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
                }
            };

            var remote = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "audio-deu", Title = "Dune Audio", Language = "deu", ReadingFormatId = 2 }
                }
            };

            var match = service.MatchRemote(local, new List<Book> { remote });

            Assert.That(match, Is.Null);
        }

        [Test]
        public void should_not_match_remote_ebook_when_retention_leaves_no_ebook_or_print_representative()
        {
            var service = new TestableRefreshBookService(
                new StubMediaFileService(),
                LogManager.GetCurrentClassLogger(),
                bookInfo: new StubBookInfo(null));

            var local = new Book
            {
                Id = 1,
                Title = "Audio Only Pocket",
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:123",
                Author = new Author
                {
                    Id = 42,
                    EbookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
                }
            };

            var remote = new Book
            {
                Title = "Audio Only Pocket",
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:123",
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "audio-eng", Title = "Audio Only", Language = "eng", ReadingFormatId = 2 }
                }
            };

            var match = service.MatchRemote(local, new List<Book> { remote });

            Assert.That(match, Is.Null);
        }

    }
}
