using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorServiceLookupHydrationFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class StubAuthorRepository : IAuthorRepository
        {
            private readonly Author _lookupAuthor;
            private readonly Author _byIdAuthor;
            public int GetCallCount { get; private set; }

            public StubAuthorRepository(Author lookupAuthor, Author byIdAuthor = null)
            {
                _lookupAuthor = lookupAuthor;
                _byIdAuthor = byIdAuthor ?? lookupAuthor;
            }

            public Author FindByName(string cleanName) => _lookupAuthor;
            public Author FindByGoodreadsId(string goodreadsId) => _lookupAuthor;
            public Author FindByHardcoverId(string hardcoverId) => _lookupAuthor;
            public Author FindByAudnexusId(string audnexusId) => _lookupAuthor;
            public Author FindByOpenLibraryId(string openLibraryId) => _lookupAuthor;
            public Author FindByGoogleBooksId(string googleBooksAuthorId) => _lookupAuthor;
            public Author Get(int id)
            {
                GetCallCount++;
                return _byIdAuthor;
            }
            public IEnumerable<Author> All() => new[] { _lookupAuthor };
            public IEnumerable<Author> Get(IEnumerable<int> ids) => new[] { _byIdAuthor };

            public int Count() => throw new NotImplementedException();
            public Author Find(int id) => throw new NotImplementedException();
            public Author Insert(Author model) => throw new NotImplementedException();
            public Author Update(Author model) => throw new NotImplementedException();
            public Author Upsert(Author model) => throw new NotImplementedException();
            public void SetFields(Author model, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Author model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<Author> model) => throw new NotImplementedException();
            public void InsertMany(IList<Author> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Author> model) => throw new NotImplementedException();
            public void SetFields(IList<Author> models, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Author> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Author Single() => throw new NotImplementedException();
            public Author SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Author> GetPaged(PagingSpec<Author> pagingSpec) => throw new NotImplementedException();
            public bool AuthorPathExists(string path) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public Dictionary<int, List<int>> AllAuthorTags() => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public bool TryClaimAuthorImport(string providerId, int leaseSec = 300) => throw new NotImplementedException();
            public void ReleaseAuthorImportClaim(string providerId) => throw new NotImplementedException();
            public int DeleteExpiredClaims() => throw new NotImplementedException();
        }

        private static AuthorService CreateSubject(IAuthorRepository repository)
        {
            return new AuthorService(
                repository,
                new StubEventAggregator(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                cacheManager: new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void find_by_name_should_return_author_with_quality_profiles_loaded()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 1,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" }),
                EbookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 1, Name = "Ebook" })
            };

            var repository = new StubAuthorRepository(lookup);
            var subject = CreateSubject(repository);

            var result = subject.FindByName("Pascale Lacelle");

            Assert.That(result, Is.SameAs(lookup));
            Assert.That(result.AudiobookQualityProfile.Value?.Id, Is.EqualTo(2));
            Assert.That(result.EbookQualityProfile.Value?.Id, Is.EqualTo(1));
            Assert.That(repository.GetCallCount, Is.EqualTo(0));
        }

        [Test]
        public void find_by_provider_id_should_return_author_with_quality_profiles_loaded()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                HardcoverAuthorId = "hc:123",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var repository = new StubAuthorRepository(lookup);
            var subject = CreateSubject(repository);

            var result = subject.FindByProviderId("hc", "123");

            Assert.That(result, Is.SameAs(lookup));
            Assert.That(result.AudiobookQualityProfile.Value?.Id, Is.EqualTo(2));
            Assert.That(repository.GetCallCount, Is.EqualTo(0));
        }

        [Test]
        public void find_by_name_should_prefer_cached_author_instance_when_available()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var cached = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var repository = new StubAuthorRepository(lookup, cached);
            var subject = CreateSubject(repository);

            Assert.That(subject.GetAuthor(1189), Is.SameAs(cached));

            var result = subject.FindByName("Pascale Lacelle");

            Assert.That(result, Is.SameAs(cached));
            Assert.That(repository.GetCallCount, Is.EqualTo(1));
        }
    }
}
