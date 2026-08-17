using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Api.V1.Calendar;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class CalendarFeedControllerFixture
    {
        [Test]
        public void should_filter_feed_by_tags_query_parameter()
        {
            var result = GetFeed(tags: "1");

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("Tagged Author - Tagged Book"));
                Assert.That(result, Does.Not.Contain("Untagged Author - Untagged Book"));
            });
        }

        [Test]
        public void should_filter_feed_by_legacy_tagList_query_parameter()
        {
            var result = GetFeed(tagList: "1");

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("Tagged Author - Tagged Book"));
                Assert.That(result, Does.Not.Contain("Untagged Author - Untagged Book"));
            });
        }

        [Test]
        public void should_prefer_tags_over_legacy_tagList_when_both_are_present()
        {
            var result = GetFeed(tags: "2", tagList: "1");

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Not.Contain("Tagged Author - Tagged Book"));
                Assert.That(result, Does.Contain("Untagged Author - Untagged Book"));
            });
        }

        private static string GetFeed(string tags = "", string tagList = "")
        {
            var authors = new[]
            {
                new Author
                {
                    Id = 1,
                    Name = "Tagged Author",
                    Tags = new HashSet<int> { 1 }
                },
                new Author
                {
                    Id = 2,
                    Name = "Untagged Author",
                    Tags = new HashSet<int> { 2 }
                }
            };

            var books = new[]
            {
                new Book
                {
                    Id = 1,
                    AuthorId = 1,
                    Title = "Tagged Book",
                    ReleaseDate = DateTime.Today.AddDays(1)
                },
                new Book
                {
                    Id = 2,
                    AuthorId = 2,
                    Title = "Untagged Book",
                    ReleaseDate = DateTime.Today.AddDays(1)
                }
            };

            var controller = new CalendarFeedController(
                new StubBookService(books),
                new StubAuthorService(authors),
                new StubEditionService(),
                new StubTagService());

            var actionResult = controller.GetCalendarFeed(tags: tags, tagList: tagList);
            var contentResult = actionResult as ContentResult;

            Assert.That(contentResult, Is.Not.Null);
            return contentResult.Content;
        }

        private sealed class StubBookService : IBookService
        {
            private readonly List<Book> _books;

            public StubBookService(IEnumerable<Book> books)
            {
                _books = books.ToList();
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
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
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => _books;
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authors;

            public StubAuthorService(IEnumerable<Author> authors)
            {
                _authors = authors.ToDictionary(author => author.Id);
            }

            public Author GetAuthor(int authorId) => _authors[authorId];
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => authorIds.Select(GetAuthor).ToList();
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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => new List<Edition>();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubTagService : ITagService
        {
            public Tag GetTag(int tagId) => new Tag { Id = tagId, Label = tagId.ToString() };
            public Tag GetTag(string tag) => GetTag(int.Parse(tag));
            public TagDetails Details(int tagId) => throw new NotImplementedException();
            public List<TagDetails> Details() => throw new NotImplementedException();
            public List<Tag> All() => throw new NotImplementedException();
            public Tag Add(Tag tag) => throw new NotImplementedException();
            public Tag Update(Tag tag) => throw new NotImplementedException();
            public void Delete(int tagId) => throw new NotImplementedException();
        }
    }
}
