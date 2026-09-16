using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Messaging.Commands;

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
            public List<Author> AllAuthorPaths() => new List<Author> { _lookupAuthor };
            public Dictionary<int, List<int>> AllAuthorTags() => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public bool TryClaimAuthorImport(string providerId, int leaseSec = 300) => throw new NotImplementedException();
            public void ReleaseAuthorImportClaim(string providerId) => throw new NotImplementedException();
            public int DeleteExpiredClaims() => throw new NotImplementedException();
        }

        private class NoOpProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotSupportedException(targetMethod.Name);
            }
        }

        private sealed class NoOpDeleteMediaFiles : IDeleteMediaFiles
        {
            public void DeleteTrackFile(Author author, BookFile bookFile) { }
            public void DeleteTrackFile(BookFile bookFile, string subfolder = "") { }
            public void DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null) { }
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
                logger: LogManager.GetCurrentClassLogger(),
                importListExclusionService: null,
                deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => null),
                bookService: new Lazy<IBookService>(() => null));
        }

        [Test]
        public void dry_ioc_should_resolve_lazy_media_file_deletion()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance<IAuthorRepository>(new StubAuthorRepository(new Author { Id = 1189, Path = @"C:\authors\Pascale Lacelle".AsOsAgnostic() }));
            container.RegisterInstance<IEventAggregator>(new StubEventAggregator());
            container.RegisterInstance<IBuildAuthorPaths>(DispatchProxy.Create<IBuildAuthorPaths, NoOpProxy>());
            container.RegisterInstance<IRootFolderService>(DispatchProxy.Create<IRootFolderService, NoOpProxy>());
            container.RegisterInstance<IManageCommandQueue>(DispatchProxy.Create<IManageCommandQueue, NoOpProxy>());
            container.RegisterInstance<ICacheManager>(new CacheManager());
            container.RegisterInstance<IBookRepository>(DispatchProxy.Create<IBookRepository, NoOpProxy>());
            container.RegisterInstance<IMediaFileService>(DispatchProxy.Create<IMediaFileService, NoOpProxy>());
            container.RegisterInstance<IImportListExclusionService>(DispatchProxy.Create<IImportListExclusionService, NoOpProxy>());
            container.RegisterInstance<IDeleteMediaFiles>(new NoOpDeleteMediaFiles());
            container.RegisterInstance<IBookService>(DispatchProxy.Create<IBookService, NoOpProxy>());
            container.Register<AuthorService>();

            var deletion = container.Resolve<Lazy<IDeleteMediaFiles>>();
            var service = container.Resolve<AuthorService>();

            Assert.That(deletion, Is.Not.Null);
            Assert.That(deletion.IsValueCreated, Is.False);
            var books = (Lazy<IBookService>)typeof(AuthorService).GetField("_bookService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            Assert.That(books, Is.Not.Null);
            Assert.That(books.IsValueCreated, Is.False);
            Assert.That(books.Value, Is.Not.Null);
            Assert.That(service.AllAuthorPaths(), Has.Count.EqualTo(1));
            Assert.That(deletion.Value, Is.TypeOf<NoOpDeleteMediaFiles>());
            Assert.That(deletion.IsValueCreated, Is.True);
        }

        [Test]
        public void all_author_paths_should_include_each_distinct_media_path()
        {
            var lookup = new Author
            {
                Id = 1189,
                Path = @"C:\legacy\Pascale Lacelle".AsOsAgnostic(),
                AudiobookPath = @"C:\audio\Pascale Lacelle".AsOsAgnostic(),
                EbookPath = @"C:\ebooks\Pascale Lacelle".AsOsAgnostic()
            };

            var result = CreateSubject(new StubAuthorRepository(lookup)).AllAuthorPaths();

            Assert.That(result.Select(path => path.Value), Is.EqualTo(new[]
            {
                @"C:\audio\Pascale Lacelle".AsOsAgnostic(),
                @"C:\ebooks\Pascale Lacelle".AsOsAgnostic(),
                @"C:\legacy\Pascale Lacelle".AsOsAgnostic()
            }));
            Assert.That(result.All(path => path.Key == lookup.Id), Is.True);
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
