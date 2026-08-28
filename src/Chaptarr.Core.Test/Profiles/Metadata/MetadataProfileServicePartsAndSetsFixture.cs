using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Releases;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class MetadataProfileServicePartsAndSetsFixture
    {
        private sealed class StubMetadataProfileRepository : IMetadataProfileRepository
        {
            private readonly Dictionary<int, MetadataProfile> _profiles;

            public StubMetadataProfileRepository(IEnumerable<MetadataProfile> profiles)
            {
                _profiles = profiles.ToDictionary(p => p.Id);
            }

            public bool Exists(int id) => _profiles.ContainsKey(id);
            public IEnumerable<MetadataProfile> All() => _profiles.Values;
            public int Count() => _profiles.Count;
            public MetadataProfile Find(int id) => _profiles.TryGetValue(id, out var profile) ? profile : null;
            public MetadataProfile Get(int id) => _profiles[id];

            public MetadataProfile Insert(MetadataProfile model) => throw new NotImplementedException();
            public MetadataProfile Update(MetadataProfile model) => throw new NotImplementedException();
            public MetadataProfile Upsert(MetadataProfile model) => throw new NotImplementedException();
            public void SetFields(MetadataProfile model, params System.Linq.Expressions.Expression<Func<MetadataProfile, object>>[] properties) => throw new NotImplementedException();
            public void Delete(MetadataProfile model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<MetadataProfile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<MetadataProfile> model) => throw new NotImplementedException();
            public void InsertMany(IList<MetadataProfile> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<MetadataProfile> model) => throw new NotImplementedException();
            public void SetFields(IList<MetadataProfile> models, params System.Linq.Expressions.Expression<Func<MetadataProfile, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<MetadataProfile> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public MetadataProfile Single() => throw new NotImplementedException();
            public MetadataProfile SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<MetadataProfile> GetPaged(PagingSpec<MetadataProfile> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public StubMediaFileService(List<BookFile> files)
            {
                _files = files ?? new List<BookFile>();
            }

            public List<BookFile> GetFilesByAuthor(int authorId) => _files;

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
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

        private sealed class StubTermMatcherService : ITermMatcherService
        {
            public bool IsMatch(string term, string value) => false;
            public string MatchingTerm(string term, string value) => null;
        }

        private static MetadataProfile CreateProfile(bool skipPartsAndSets = true)
        {
            return new MetadataProfile
            {
                Id = 1,
                Name = "Test",
                ProfileType = MetadataProfileType.General,
                MinPopularity = 0,
                SkipMissingDate = false,
                SkipMissingIsbn = false,
                SkipMissingAsin = false,
                SkipPartsAndSets = skipPartsAndSets,
                SkipSeriesSecondary = false,
                SkipMissingIdentifierOmnibus = false,
                SkipOmnibus = false,
                MinPages = 0,
                Ignored = new List<string>()
            };
        }

        private static Book CreateBook(string title, string seriesName, string seriesPosition)
        {
            return new Book
            {
                Title = title,
                HardcoverBookId = "2509944",
                MediaType = BookMediaType.Ebook,
                SeriesName = seriesName,
                SeriesPosition = seriesPosition,
                Editions = new List<Edition> { new Edition { ForeignEditionId = "edition-1", Title = title } }
            };
        }

        private static List<Book> Filter(MetadataProfile profile, Author author, List<BookFile> localFiles = null)
        {
            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: null,
                bookService: null,
                editionService: null,
                mediaFileService: new StubMediaFileService(localFiles ?? new List<BookFile>()),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            return service.FilterBooks(author, profile.Id);
        }

        [Test]
        public void should_skip_book_whose_series_position_is_a_split_volume()
        {
            var book = CreateBook("Harry Potter and the Prisoner of Azkaban",
                                  "Harry Potter Japanese Split-Volume Children's Edition",
                                  "3, Part 1 of 2");

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } });

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_skip_book_whose_series_position_is_a_lettered_part()
        {
            var book = CreateBook("Harry Potter und die Kammer des Schreckens",
                                  "Harry Potter Japanese Split-Volume Children's Edition",
                                  "2A");

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } });

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_skip_split_volume_even_when_the_series_link_position_is_clean()
        {
            // The relational SeriesBookLink rows are routinely cleaner than the provider data they
            // were built from, so the link alone says "book 3" while the book itself says "3, Part 1 of 2".
            var book = CreateBook("Harry Potter and the Prisoner of Azkaban",
                                  "Harry Potter Japanese Split-Volume Children's Edition",
                                  "3, Part 1 of 2");

            var link = new SeriesBookLink { Position = "3", IsPrimary = true, Book = book };
            var series = new Series { Title = "Harry Potter", LinkItems = new List<SeriesBookLink> { link } };

            var author = new Author
            {
                Books = new List<Book> { book },
                Series = new List<Series> { series }
            };

            var result = Filter(CreateProfile(), author);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_keep_book_whose_series_position_is_a_whole_number()
        {
            var book = CreateBook("A Clash of Kings", "A Song of Ice and Fire", "2");

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } });

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_book_whose_series_position_is_fractional()
        {
            // Fractional positions are legitimate novellas, not split volumes.
            var book = CreateBook("The Hedge Knight", "A Song of Ice and Fire", "0.5");

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } });

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_book_that_has_no_series_position()
        {
            var book = CreateBook("Fevre Dream", null, null);

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } });

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_split_volume_when_parts_and_sets_filter_is_disabled()
        {
            var book = CreateBook("Harry Potter and the Prisoner of Azkaban",
                                  "Harry Potter Japanese Split-Volume Children's Edition",
                                  "3, Part 1 of 2");

            var result = Filter(CreateProfile(skipPartsAndSets: false), new Author { Books = new List<Book> { book } });

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_split_volume_that_is_already_on_disk()
        {
            var book = CreateBook("Harry Potter and the Prisoner of Azkaban",
                                  "Harry Potter Japanese Split-Volume Children's Edition",
                                  "3, Part 1 of 2");

            var owned = CreateBook("Harry Potter and the Prisoner of Azkaban",
                                   "Harry Potter Japanese Split-Volume Children's Edition",
                                   "3, Part 1 of 2");

            var localFile = new BookFile
            {
                Path = "/ebooks/J.K. Rowling/Harry Potter and the Prisoner of Azkaban/book.epub",
                Edition = new Edition { ForeignEditionId = "edition-1", Book = owned }
            };

            var result = Filter(CreateProfile(), new Author { Books = new List<Book> { book } }, new List<BookFile> { localFile });

            Assert.That(result, Has.Count.EqualTo(1));
        }
    }
}
