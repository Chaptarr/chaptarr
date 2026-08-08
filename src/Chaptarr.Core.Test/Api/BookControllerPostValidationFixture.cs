using System;
using System.Collections.Generic;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerPostValidationFixture
    {
        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Func<int, Author> _getAuthor;
            private readonly Func<string, string, Author> _findByProviderId;

            public StubAuthorService(Func<int, Author> getAuthor, Func<string, string, Author> findByProviderId)
            {
                _getAuthor = getAuthor;
                _findByProviderId = findByProviderId;
            }

            public Author FindByProviderId(string provider, string providerId) => _findByProviderId?.Invoke(provider, providerId);

            public Author GetAuthor(int authorId) => _getAuthor?.Invoke(authorId);
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
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

        private sealed class TestableBookController : Chaptarr.Api.V1.Books.BookController
        {
            public TestableBookController(IAuthorService authorService, MetadataProfileExistsValidator metadataProfileExistsValidator, Logger logger)
                : base(authorService: authorService,
                    bookService: null,
                    addBookService: null,
                    editionService: null,
                    editionSelector: null,
                    seriesBookLinkService: null,
                    authorStatisticsService: null,
                    mediaFileService: null,
                    coverMapper: null,
                    upgradableSpecification: null,
                    signalRBroadcaster: null,
                    commandQueueManager: null,
                    eventAggregator: null,
                    metadataProfileService: null,
                    qualityProfileService: null,
                    rootFolderService: null,
                    qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                    metadataProfileExistsValidator: metadataProfileExistsValidator ?? new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    logger: logger)
            {
            }

            public ValidationResult ValidatePost(BookResource resource)
            {
                return PostValidator.Validate(resource);
            }
        }

        [Test]
        public void should_allow_existing_author_without_quality_profiles()
        {
            var controller = new TestableBookController(
                new StubAuthorService(authorId => authorId == 123 ? new Author { Id = 123 } : null, (_, _) => null),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                LogManager.GetCurrentClassLogger());

            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    Id = 123,
                    ForeignAuthorId = "hc:191785"
                }
            };

            var result = controller.ValidatePost(resource);

            Assert.That(result.IsValid, Is.True, () => string.Join(Environment.NewLine, result.Errors));
        }

        [Test]
        public void should_allow_author_matched_by_foreign_id_without_quality_profiles()
        {
            var controller = new TestableBookController(
                new StubAuthorService(getAuthor: null, findByProviderId: (provider, providerId) => provider == "hc" && providerId == "191785" ? new Author { Id = 123 } : null),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                LogManager.GetCurrentClassLogger());

            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    Id = 0,
                    ForeignAuthorId = "hc:191785"
                }
            };

            var result = controller.ValidatePost(resource);

            Assert.That(result.IsValid, Is.True, () => string.Join(Environment.NewLine, result.Errors));
        }

        [Test]
        public void should_reject_new_author_without_quality_profiles()
        {
            var controller = new TestableBookController(
                new StubAuthorService(getAuthor: null, findByProviderId: (_, _) => null),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                LogManager.GetCurrentClassLogger());

            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    Id = 0,
                    ForeignAuthorId = "hc:999999"
                }
            };

            var result = controller.ValidatePost(resource);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "At least one quality profile must be selected"));
        }

        [TestCase("30643037")]
        [TestCase("B000TEST01")]
        public void should_reject_native_book_import_with_bare_foreign_edition_id(string foreignEditionId)
        {
            var controller = new TestableBookController(
                new StubAuthorService(getAuthor: null, findByProviderId: (_, _) => null),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var resource = new BookImportResource
            {
                ForeignBookId = "hc:2514970",
                ForeignAuthorId = "hc:149559",
                ForeignEditionId = foreignEditionId,
                MediaType = "ebook"
            };

            var ex = Assert.ThrowsAsync<ValidationException>(async () => await controller.ImportBook(resource));

            Assert.That(ex.Errors, Has.Some.Matches<ValidationFailure>(failure =>
                failure.PropertyName == "ForeignEditionId" &&
                failure.ErrorMessage.Contains("provider prefix")));
        }
    }
}
