using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Abstractions;
using System.Linq;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
	    public class FormatScopedUnitCloneFixture
	    {
	        private sealed class StubMainDatabase : IMainDatabase
	        {
	            public IDbConnection OpenConnection()
	            {
	                var connection = new SqliteConnection("DataSource=:memory:");
	                connection.Open();
	                return connection;
	            }

	            public Version Version => new(0, 0);
	            public int Migration => 0;
	            public DatabaseType DatabaseType => DatabaseType.SQLite;
	            public void Vacuum()
	            {
	            }
	        }

	        private sealed class StubMediaFileService : IMediaFileService
	        {
            private readonly Dictionary<int, List<BookFile>> _filesByBookId;

            public StubMediaFileService(Dictionary<int, List<BookFile>> filesByBookId)
            {
                _filesByBookId = filesByBookId ?? new Dictionary<int, List<BookFile>>();
            }

            public List<BookFile> GetFilesByBook(int bookId)
            {
                return _filesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
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

        private sealed class InMemoryBookService : IBookService
        {
            private readonly Dictionary<int, Book> _booksById = new Dictionary<int, Book>();
            private readonly Dictionary<string, int> _idBySlug = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private int _nextId = 1000;

            public InMemoryBookService(IEnumerable<Book> seed)
            {
                foreach (var b in seed ?? Array.Empty<Book>())
                {
                    if (b == null) continue;
                    _booksById[b.Id] = b;
                    if (!string.IsNullOrWhiteSpace(b.TitleSlug))
                    {
                        _idBySlug[b.TitleSlug] = b.Id;
                    }
                }
            }

            public Book GetBook(int bookId) => _booksById.TryGetValue(bookId, out var b) ? b : null;

            public void InsertMany(List<Book> books)
            {
                foreach (var b in books ?? new List<Book>())
                {
                    if (b == null) continue;
                    if (b.Id <= 0) b.Id = _nextId++;
                    _booksById[b.Id] = b;
                    if (!string.IsNullOrWhiteSpace(b.TitleSlug))
                    {
                        _idBySlug[b.TitleSlug] = b.Id;
                    }
                }
            }

            public Book FindBySlug(string titleSlug)
            {
                if (string.IsNullOrWhiteSpace(titleSlug)) return null;
                return _idBySlug.TryGetValue(titleSlug, out var id) ? GetBook(id) : null;
            }

            public Book FindByTitle(int authorId, string title)
            {
                if (string.IsNullOrWhiteSpace(title)) return null;
                return _booksById.Values.FirstOrDefault(b =>
                    b != null &&
                    b.AuthorId == authorId &&
                    string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase));
            }

            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId)
            {
                return _booksById.Values
                    .Where(b => b != null && b.AuthorId == authorId)
                    .ToList();
            }
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
	            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
	            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
	            {
	                InsertMany(books);
	            }
	            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
	            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class InMemoryEditionService : IEditionService
        {
            private readonly Dictionary<int, Edition> _editionsById = new Dictionary<int, Edition>();
            private readonly Dictionary<int, List<Edition>> _editionsByBookId = new Dictionary<int, List<Edition>>();
            private int _nextId = 2000;

            public InMemoryEditionService(IEnumerable<Edition> seed)
            {
                foreach (var e in seed ?? Array.Empty<Edition>())
                {
                    if (e == null) continue;
                    _editionsById[e.Id] = e;
                    if (!_editionsByBookId.TryGetValue(e.BookId, out var list))
                    {
                        list = new List<Edition>();
                        _editionsByBookId[e.BookId] = list;
                    }
                    list.Add(e);
                }
            }

            public Edition GetEdition(int id) => _editionsById.TryGetValue(id, out var e) ? e : null;

            public List<Edition> GetEditionsByBook(int bookId)
            {
                return _editionsByBookId.TryGetValue(bookId, out var list) ? list.ToList() : new List<Edition>();
            }

            public void InsertMany(List<Edition> editions)
            {
                foreach (var e in editions ?? new List<Edition>())
                {
                    if (e == null) continue;
                    if (e.Id <= 0) e.Id = _nextId++;
                    _editionsById[e.Id] = e;
                    if (!_editionsByBookId.TryGetValue(e.BookId, out var list))
                    {
                        list = new List<Edition>();
                        _editionsByBookId[e.BookId] = list;
                    }
                    list.Add(e);
                }
            }

            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
            {
                InsertMany(editions);
            }
	            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
	            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        [Test]
        public void should_clone_when_same_root_has_different_media_extensions()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 1758,
                AuthorId = 32,
                Title = "Harry Potter and the Prisoner of Azkaban",
                TitleSlug = "harry-potter-and-the-prisoner-of-azkaban",
                MediaType = BookMediaType.Audiobook
            };

            var editionM4b = new Edition
            {
                Id = 4608,
                BookId = canonicalBook.Id,
                Title = canonicalBook.Title,
                TitleSlug = "hp3-us"
            };

            var editionMp3 = new Edition
            {
                Id = 4613,
                BookId = canonicalBook.Id,
                Title = canonicalBook.Title,
                TitleSlug = "hp3-uk"
            };

            var existingM4bFile = new BookFile
            {
                EditionId = editionM4b.Id,
                Path = "/audiobooks/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Prisoner of Azkaban/Harry Potter and the Prisoner of Azkaban.m4b"
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>
            {
                [canonicalBook.Id] = new List<BookFile> { existingM4bFile }
            });

            var bookService = new InMemoryBookService(new[] { canonicalBook });
            var editionService = new InMemoryEditionService(new[] { editionM4b, editionMp3 });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            string BuildDestKey(string filePath) =>
                unitDestination.BuildRootUnitKeyWithExtension(filePath, canonicalBook.Title, canonicalBook.MediaType);

            // Same extension → reuse canonical destination.
            var destM4b = unitDestination.ResolveDestinationForUnit(canonicalBook, editionM4b, BuildDestKey(existingM4bFile.Path));
            Assert.That(destM4b.Item1, Is.EqualTo(canonicalBook.Id));
            Assert.That(destM4b.Item2, Is.EqualTo(editionM4b.Id));

            // Different extension (same root) → clone to avoid mixing media file types under one book.
            var mp3Path = existingM4bFile.Path.Replace(".m4b", ".mp3", StringComparison.OrdinalIgnoreCase);
            var destMp3 = unitDestination.ResolveDestinationForUnit(canonicalBook, editionMp3, BuildDestKey(mp3Path));
            Assert.That(destMp3.Item1, Is.Not.EqualTo(canonicalBook.Id));
            Assert.That(destMp3.Item2, Is.Not.EqualTo(editionMp3.Id));
        }

        [Test]
        public void should_reuse_existing_file_bearing_clone_when_another_identity_group_copy_already_owns_the_unit()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 1900,
                AuthorId = 44,
                Title = "Dreamsongs",
                TitleSlug = "dreamsongs",
                BaseBookId = "dreamsongs",
                MediaType = BookMediaType.Audiobook
            };

            var filelessCanonicalBook = new Book
            {
                Id = 1902,
                AuthorId = 44,
                Title = "Dreamsongs",
                TitleSlug = "dreamsongs-fileless",
                BaseBookId = "dreamsongs",
                MediaType = BookMediaType.Audiobook
            };

            var cloneBook = new Book
            {
                Id = 1901,
                AuthorId = 44,
                Title = "Dreamsongs",
                TitleSlug = "dreamsongs_copy",
                BaseBookId = "dreamsongs",
                MediaType = BookMediaType.Audiobook
            };

            var canonicalEdition = new Edition
            {
                Id = 5000,
                BookId = canonicalBook.Id,
                Title = canonicalBook.Title,
                TitleSlug = "dreamsongs-us",
                ForeignEditionId = "gr:ed:dreamsongs-us"
            };

            var cloneEdition = new Edition
            {
                Id = 5001,
                BookId = cloneBook.Id,
                Title = cloneBook.Title,
                TitleSlug = "dreamsongs-us-copy",
                ForeignEditionId = "gr:ed:dreamsongs-us"
            };

            var canonicalFile = new BookFile
            {
                EditionId = canonicalEdition.Id,
                Path = "/audiobooks/George R. R. Martin/Dreamsongs/Dreamsongs.m4b"
            };

            var cloneFile = new BookFile
            {
                EditionId = cloneEdition.Id,
                Path = "/audiobooks/George R. R. Martin/Dreamsongs (Alt)/Dreamsongs.m4b"
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>
            {
                [canonicalBook.Id] = new List<BookFile> { canonicalFile },
                [cloneBook.Id] = new List<BookFile> { cloneFile }
            });

            var bookService = new InMemoryBookService(new[] { canonicalBook, cloneBook, filelessCanonicalBook });
            var editionService = new InMemoryEditionService(new[] { canonicalEdition, cloneEdition });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var cloneUnitKey = unitDestination.BuildRootUnitKeyWithExtension(cloneFile.Path, canonicalBook.Title, canonicalBook.MediaType);

            var destination = unitDestination.ResolveDestinationForUnit(filelessCanonicalBook, canonicalEdition, cloneUnitKey);

            Assert.That(destination.Item1, Is.EqualTo(cloneBook.Id), "existing file-bearing clone should own its unit instead of forcing a fresh clone");
            Assert.That(destination.Item2, Is.EqualTo(cloneEdition.Id));
        }

        [Test]
        public void should_prefer_existing_empty_targeted_clone_with_matching_edition_identity_over_fileless_generic_canonical()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 2000,
                AuthorId = 55,
                Title = "Dune",
                TitleSlug = "dune",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var targetedClone = new Book
            {
                Id = 2001,
                AuthorId = 55,
                Title = "Dune",
                TitleSlug = "dune_wanted_42",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                WantedNarratorId = 42
            };

            var canonicalEdition = new Edition
            {
                Id = 6000,
                BookId = canonicalBook.Id,
                Title = "Dune",
                TitleSlug = "dune-main",
                ForeignEditionId = "az:ed:dune-main"
            };

            var targetedEdition = new Edition
            {
                Id = 6001,
                BookId = targetedClone.Id,
                Title = "Dune",
                TitleSlug = "dune-main-targeted",
                ForeignEditionId = "az:ed:dune-main",
                Monitored = true
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>());
            var bookService = new InMemoryBookService(new[] { canonicalBook, targetedClone });
            var editionService = new InMemoryEditionService(new[] { canonicalEdition, targetedEdition });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Dune.m4b", canonicalBook.Title, canonicalBook.MediaType);
            var destination = unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, unitKey);

            Assert.That(destination.Item1, Is.EqualTo(targetedClone.Id), "empty targeted clone should win over a fileless generic canonical row when it carries the matched edition");
            Assert.That(destination.Item2, Is.EqualTo(targetedEdition.Id));
        }

        [Test]
        public void should_not_route_to_empty_targeted_clone_without_exact_matching_edition_identity()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 2010,
                AuthorId = 56,
                Title = "Dune",
                TitleSlug = "dune",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var targetedClone = new Book
            {
                Id = 2011,
                AuthorId = 56,
                Title = "Dune",
                TitleSlug = "dune_wanted_43",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                WantedNarratorId = 43
            };

            var canonicalEdition = new Edition
            {
                Id = 6010,
                BookId = canonicalBook.Id,
                Title = "Dune",
                TitleSlug = "dune-main",
                ForeignEditionId = "az:ed:dune-main"
            };

            var placeholderTargetedEdition = new Edition
            {
                Id = 6011,
                BookId = targetedClone.Id,
                Title = "Dune",
                TitleSlug = "dune-placeholder",
                ForeignEditionId = "2011_edition",
                Monitored = true
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>());
            var bookService = new InMemoryBookService(new[] { canonicalBook, targetedClone });
            var editionService = new InMemoryEditionService(new[] { canonicalEdition, placeholderTargetedEdition });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Dune.m4b", canonicalBook.Title, canonicalBook.MediaType);
            var destination = unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, unitKey);

            Assert.That(destination.Item1, Is.EqualTo(canonicalBook.Id), "targeted clones without an exact edition identity match should not hijack the destination");
            Assert.That(destination.Item2, Is.EqualTo(canonicalEdition.Id));
        }

        [Test]
        public void should_clone_a_conflicting_match_instead_of_consuming_the_user_pinned_book_row()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var canonicalBook = new Book
            {
                Id = 2015,
                AuthorId = 56,
                Title = "Dune",
                TitleSlug = "dune-pinned",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false
            };
            var pinnedEdition = new Edition
            {
                Id = 6015,
                BookId = canonicalBook.Id,
                Title = "Dune (Pinned Narration)",
                ForeignEditionId = "az:ed:dune-pinned",
                Monitored = true,
                ManualAdd = false
            };
            var matchedEdition = new Edition
            {
                Id = 6016,
                BookId = canonicalBook.Id,
                Title = "Dune (Other Narration)",
                ForeignEditionId = "az:ed:dune-other"
            };
            var bookService = new InMemoryBookService(new[] { canonicalBook });
            var editionService = new InMemoryEditionService(new[] { pinnedEdition, matchedEdition });
            var unitDestination = new BookUnitDestinationService(
                new StubMediaFileService(new Dictionary<int, List<BookFile>>()),
                bookService,
                editionService,
                new StubMainDatabase(),
                logger);

            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Dune.m4b", matchedEdition.Title, canonicalBook.MediaType);
            var destination = unitDestination.ResolveDestinationForUnit(canonicalBook, matchedEdition, unitKey);
            var copy = bookService.GetBook(destination.BookId);
            var copyEditions = editionService.GetEditionsByBook(destination.BookId);

            Assert.That(destination.BookId, Is.Not.EqualTo(canonicalBook.Id));
            Assert.That(copy.AnyEditionOk, Is.True, "an automatic copy must not inherit the user's pin");
            Assert.That(copyEditions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { matchedEdition.ForeignEditionId }));
            Assert.That(copyEditions.Single().Monitored, Is.True);
            Assert.That(copyEditions.Single().ManualAdd, Is.False);
            Assert.That(pinnedEdition.Monitored, Is.True);
            Assert.That(pinnedEdition.ManualAdd, Is.False);
        }

        [Test]
        public void cached_canonical_destination_must_not_bypass_a_pin_added_later()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var canonicalBook = new Book
            {
                Id = 2017,
                AuthorId = 56,
                Title = "Dune",
                TitleSlug = "dune",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };
            var currentEdition = new Edition
            {
                Id = 6017,
                BookId = canonicalBook.Id,
                Title = "Dune (Current)",
                ForeignEditionId = "az:ed:dune-current",
                Monitored = true
            };
            var matchedEdition = new Edition
            {
                Id = 6018,
                BookId = canonicalBook.Id,
                Title = "Dune (Other Narration)",
                ForeignEditionId = "az:ed:dune-other"
            };
            var bookService = new InMemoryBookService(new[] { canonicalBook });
            var editionService = new InMemoryEditionService(new[] { currentEdition, matchedEdition });
            var unitDestination = new BookUnitDestinationService(
                new StubMediaFileService(new Dictionary<int, List<BookFile>>()),
                bookService,
                editionService,
                new StubMainDatabase(),
                logger);
            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Dune.m4b", matchedEdition.Title, canonicalBook.MediaType);

            var beforePin = unitDestination.ResolveDestinationForUnit(canonicalBook, matchedEdition, unitKey);
            Assert.That(beforePin.BookId, Is.EqualTo(canonicalBook.Id));

            canonicalBook.AnyEditionOk = false;
            currentEdition.ManualAdd = true;
            var afterPin = unitDestination.ResolveDestinationForUnit(canonicalBook, matchedEdition, unitKey);

            Assert.That(afterPin.BookId, Is.Not.EqualTo(canonicalBook.Id));
            Assert.That(currentEdition.Monitored, Is.True);
            Assert.That(currentEdition.ManualAdd, Is.True);
        }

        [Test]
        public void should_clone_only_the_selected_edition_for_new_unit_destinations()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 2020,
                AuthorId = 57,
                Title = "Dune",
                TitleSlug = "dune",
                BaseBookId = "dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var selectedEdition = new Edition
            {
                Id = 6020,
                BookId = canonicalBook.Id,
                Title = "Dune",
                TitleSlug = "dune-main",
                ForeignEditionId = "az:ed:dune-main",
                AudibleASIN = "B000TEST01"
            };

            var siblingEdition = new Edition
            {
                Id = 6021,
                BookId = canonicalBook.Id,
                Title = "Dune (Alt)",
                TitleSlug = "dune-alt",
                ForeignEditionId = "az:ed:dune-alt"
            };

            var canonicalFile = new BookFile
            {
                EditionId = selectedEdition.Id,
                Path = "/audiobooks/Frank Herbert/Dune/Dune.m4b"
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>
            {
                [canonicalBook.Id] = new List<BookFile> { canonicalFile }
            });
            var bookService = new InMemoryBookService(new[] { canonicalBook });
            var editionService = new InMemoryEditionService(new[] { selectedEdition, siblingEdition });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Frank Herbert/Dune/Dune.mp3", canonicalBook.Title, canonicalBook.MediaType);
            var destination = unitDestination.ResolveDestinationForUnit(canonicalBook, selectedEdition, unitKey);
            var clonedBook = bookService.GetBook(destination.Item1);
            var clonedEditions = editionService.GetEditionsByBook(destination.Item1);

            Assert.That(destination.Item1, Is.Not.EqualTo(canonicalBook.Id));
            Assert.That(clonedBook.ForeignEditionId, Is.EqualTo(selectedEdition.ForeignEditionId));
            Assert.That(clonedBook.AudibleASIN, Is.EqualTo(selectedEdition.AudibleASIN));
            Assert.That(clonedEditions.Select(e => e.ForeignEditionId), Is.EqualTo(new[] { selectedEdition.ForeignEditionId }));
        }

        [Test]
        public void should_group_audio_container_keys_for_same_folder_files()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var unitDestination = new BookUnitDestinationService(
                mediaFileService: new StubMediaFileService(new Dictionary<int, List<BookFile>>()),
                bookService: new InMemoryBookService(Array.Empty<Book>()),
                editionService: new InMemoryEditionService(Array.Empty<Edition>()),
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var alpha = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Alpha.m4b", "Alpha", BookMediaType.Audiobook);
            var beta = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Beta.m4b", "Beta", BookMediaType.Audiobook);
            var alphaEbook = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Alpha.epub", "Alpha", BookMediaType.Ebook);
            var betaEbook = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Beta.epub", "Beta", BookMediaType.Ebook);

            Assert.That(alpha, Is.EqualTo(beta));
            Assert.That(alphaEbook, Is.Not.EqualTo(betaEbook));
        }

        [Test]
        public void should_consider_provider_identity_matches_even_when_base_book_id_differs()
        {
            var logger = LogManager.GetCurrentClassLogger();

            var canonicalBook = new Book
            {
                Id = 2030,
                AuthorId = 58,
                Title = "Dune",
                TitleSlug = "dune-generic",
                BaseBookId = "dune-generic",
                GoodreadsWorkId = "gr:100",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var targetedClone = new Book
            {
                Id = 2031,
                AuthorId = 58,
                Title = "Dune",
                TitleSlug = "dune-targeted",
                BaseBookId = "dune-targeted",
                GoodreadsWorkId = "gr:100",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                WantedNarratorId = 77
            };

            var canonicalEdition = new Edition
            {
                Id = 6030,
                BookId = canonicalBook.Id,
                Title = "Dune",
                TitleSlug = "dune-main",
                ForeignEditionId = "az:ed:dune-main"
            };

            var targetedEdition = new Edition
            {
                Id = 6031,
                BookId = targetedClone.Id,
                Title = "Dune",
                TitleSlug = "dune-main-targeted",
                ForeignEditionId = "az:ed:dune-main",
                Monitored = true
            };

            var mediaFileService = new StubMediaFileService(new Dictionary<int, List<BookFile>>());
            var bookService = new InMemoryBookService(new[] { canonicalBook, targetedClone });
            var editionService = new InMemoryEditionService(new[] { canonicalEdition, targetedEdition });

            var unitDestination = new BookUnitDestinationService(
                mediaFileService: mediaFileService,
                bookService: bookService,
                editionService: editionService,
                mainDatabase: new StubMainDatabase(),
                logger: logger);

            var unitKey = unitDestination.BuildRootUnitKeyWithExtension("/incoming/Dune/Dune.m4b", canonicalBook.Title, canonicalBook.MediaType);
            var destination = unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, unitKey);

            Assert.That(destination.Item1, Is.EqualTo(targetedClone.Id));
            Assert.That(destination.Item2, Is.EqualTo(targetedEdition.Id));
        }
    }
}
