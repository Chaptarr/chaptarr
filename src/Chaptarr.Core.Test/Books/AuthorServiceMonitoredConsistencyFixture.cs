using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorServiceMonitoredConsistencyFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public Action<IEvent> OnPublish { get; set; }

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                OnPublish?.Invoke(@event);
            }
        }

        private sealed class StubAuthorRepository : IAuthorRepository
        {
            private readonly Dictionary<int, Author> _authors;

            public Author LastUpdatedAuthor { get; private set; }
            public int DeleteManyCalls { get; private set; }

            public StubAuthorRepository(Dictionary<int, Author> authors)
            {
                _authors = authors ?? new Dictionary<int, Author>();
            }

            public Author Get(int id) => _authors.TryGetValue(id, out var author) ? author : null;
            public IEnumerable<Author> All() => _authors.Values;
            public IEnumerable<Author> Get(IEnumerable<int> ids) => ids?.Select(id => Get(id)).Where(author => author != null).ToList() ?? new List<Author>();

            public Author Update(Author model)
            {
                LastUpdatedAuthor = model;
                _authors[model.Id] = model;
                return model;
            }

            // Unused members for this fixture
            public int Count() => throw new NotImplementedException();
            public Author Find(int id) => throw new NotImplementedException();
            public Author Insert(Author model) => throw new NotImplementedException();
            public Author Upsert(Author model) => throw new NotImplementedException();
            public void SetFields(Author model, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Author model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<Author> model) => throw new NotImplementedException();
            public void InsertMany(IList<Author> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Author> model) => throw new NotImplementedException();
            public void SetFields(IList<Author> models, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Author> model) => DeleteMany(model.Select(author => author.Id));
            public void DeleteMany(IEnumerable<int> ids)
            {
                DeleteManyCalls++;
                foreach (var id in ids ?? Enumerable.Empty<int>())
                {
                    _authors.Remove(id);
                }
            }
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Author Single() => throw new NotImplementedException();
            public Author SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Author> GetPaged(PagingSpec<Author> pagingSpec) => throw new NotImplementedException();

            public bool AuthorPathExists(string path) => throw new NotImplementedException();
            public Author FindByName(string cleanName) => throw new NotImplementedException();
            public Author FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Author FindByHardcoverId(string hardcoverId) => throw new NotImplementedException();
            public Author FindByAudnexusId(string audnexusId) => throw new NotImplementedException();
            public Author FindByOpenLibraryId(string openLibraryId) => throw new NotImplementedException();
            public Author FindByGoogleBooksId(string googleBooksAuthorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public Dictionary<int, List<int>> AllAuthorTags() => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public bool TryClaimAuthorImport(string providerId, int leaseSec = 300) => throw new NotImplementedException();
            public void ReleaseAuthorImportClaim(string providerId) => throw new NotImplementedException();
            public int DeleteExpiredClaims() => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService()
            {
                _rootFolders = new List<RootFolder>
                {
                    new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
                };
            }

            public List<RootFolder> All() => _rootFolders.ToList();
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => _rootFolders.FirstOrDefault(r => r.Id == id);
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => _rootFolders.FirstOrDefault(r => r.Path == path);
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => allRootFolders?.FirstOrDefault(r => r.Path == path);
            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;
        }

        private static AuthorService BuildService(StubAuthorRepository repository, IRootFolderService rootFolderService = null, StubEventAggregator eventAggregator = null)
        {
            return new AuthorService(
                repository,
                eventAggregator ?? new StubEventAggregator(),
                authorPathBuilder: null,
                rootFolderService: rootFolderService,
                commandQueueManager: null,
                cacheManager: new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void delete_authors_should_keep_all_parents_until_events_finish_then_delete_in_one_batch()
        {
            var first = new Author { Id = 1, Name = "First" };
            var second = new Author { Id = 2, Name = "Second" };
            var repository = new StubAuthorRepository(new Dictionary<int, Author>
            {
                { first.Id, first },
                { second.Id, second }
            });
            var events = new StubEventAggregator();
            events.OnPublish = published =>
            {
                if (published is AuthorDeletedEvent)
                {
                    Assert.That(repository.Get(first.Id), Is.Not.Null);
                    Assert.That(repository.Get(second.Id), Is.Not.Null);
                }
            };
            var service = BuildService(repository, eventAggregator: events);

            service.DeleteAuthors(new List<int> { first.Id, second.Id, second.Id }, deleteFiles: false);

            Assert.That(repository.Get(first.Id), Is.Null);
            Assert.That(repository.Get(second.Id), Is.Null);
            Assert.That(repository.DeleteManyCalls, Is.EqualTo(1));
        }

        [Test]
        public void update_author_should_recompute_monitored_from_media_settings()
        {
            var stored = new Author
            {
                Id = 1,
                Name = "Test Author",
                Monitored = false,
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false
            };

            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { stored.Id, stored } });
            var service = BuildService(repository);

            var update = new Author
            {
                Id = 1,
                Name = "Test Author",
                Monitored = false, // stale legacy value from client
                AudiobookMonitorExisting = 1, // user switched to "All"
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false
            };

            service.UpdateAuthor(update);

            Assert.That(repository.LastUpdatedAuthor.Monitored, Is.True);
        }

        [Test]
        public void update_author_should_set_monitored_false_when_all_media_disabled()
        {
            var stored = new Author
            {
                Id = 2,
                Name = "Another Author",
                Monitored = true,
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 1,
                EbookMonitorFuture = true
            };

            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { stored.Id, stored } });
            var service = BuildService(repository);

            var update = new Author
            {
                Id = 2,
                Name = "Another Author",
                Monitored = true, // stale legacy value from client
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false
            };

            service.UpdateAuthor(update);

            Assert.That(repository.LastUpdatedAuthor.Monitored, Is.False);
        }

        [Test]
        public void update_author_should_not_persist_known_provider_placeholder_images()
        {
            const string placeholder = "https://assets.hardcover.app/author/910002/provider-default.jpg";
            const string realPhoto = "https://images.example/real-author.jpg";
            NzbDrone.Core.MediaCover.MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e");
            var stored = new Author
            {
                Id = 910002,
                Name = "Example Author"
            };
            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { stored.Id, stored } });
            var service = BuildService(repository);
            var update = new Author
            {
                Id = stored.Id,
                Name = stored.Name,
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(NzbDrone.Core.MediaCover.MediaCoverTypes.Poster, placeholder),
                    new(NzbDrone.Core.MediaCover.MediaCoverTypes.Poster, realPhoto)
                }
            };

            service.UpdateAuthor(update);

            Assert.That(repository.LastUpdatedAuthor.Images.Select(image => image.Url), Is.EqualTo(new[] { realPhoto }));
        }

        [Test]
        public void update_author_should_seed_opposite_monitor_existing_selected_when_sync_is_enabled()
        {
            var stored = new Author
            {
                Id = 3,
                Name = "Sync Author",
                SyncMonitoredAcrossFormats = false,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks"
            };

            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { stored.Id, stored } });
            var service = BuildService(repository, new StubRootFolderService());

            var update = new Author
            {
                Id = 3,
                Name = "Sync Author",
                SyncMonitoredAcrossFormats = true,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks"
            };

            service.UpdateAuthor(update);

            Assert.That(repository.LastUpdatedAuthor.AudiobookMonitorExisting, Is.EqualTo(2));
            Assert.That(repository.LastUpdatedAuthor.EbookMonitorExisting, Is.EqualTo(2));
        }

        [Test]
        public void update_author_should_not_reseed_monitor_existing_after_sync_is_already_enabled()
        {
            var stored = new Author
            {
                Id = 4,
                Name = "Sync Author",
                SyncMonitoredAcrossFormats = true,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 2,
                EbookMonitorFuture = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks"
            };

            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { stored.Id, stored } });
            var service = BuildService(repository, new StubRootFolderService());

            var update = new Author
            {
                Id = 4,
                Name = "Sync Author",
                SyncMonitoredAcrossFormats = true,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks"
            };

            service.UpdateAuthor(update);

            Assert.That(repository.LastUpdatedAuthor.AudiobookMonitorExisting, Is.EqualTo(2));
            Assert.That(repository.LastUpdatedAuthor.EbookMonitorExisting, Is.EqualTo(0));
        }

        [Test]
        public void progressive_update_should_fill_missing_quality_profile_without_overwriting_existing_manual_media_settings()
        {
            var author = new Author
            {
                Id = 5,
                Name = "Manual Settings Author",
                EbookMetadataProfileId = 88,
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true,
                EbookRootFolderPath = "/ebooks/manual"
            };
            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { author.Id, author } });
            var service = BuildService(repository);

            var result = service.UpdateAuthorProgressiveSettings(
                author,
                audiobookQualityProfileId: null,
                audiobookMetadataProfileId: null,
                audiobookMonitorExisting: null,
                audiobookMonitorFuture: null,
                ebookQualityProfileId: 3,
                ebookMetadataProfileId: 4,
                ebookMonitorExisting: 0,
                ebookMonitorFuture: false,
                rootFolderPath: "/ebooks/root-default");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.SameAs(repository.LastUpdatedAuthor));
                Assert.That(author.EbookQualityProfileId, Is.EqualTo(3));
                Assert.That(author.EbookMetadataProfileId, Is.EqualTo(88));
                Assert.That(author.EbookMonitorExisting, Is.EqualTo(2));
                Assert.That(author.EbookMonitorFuture, Is.True);
                Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks/manual"));
                Assert.That(repository.LastUpdatedAuthor, Is.Not.Null);
            });
        }

        [TestCase("audiobook", null)]
        [TestCase("audiobook", 0)]
        [TestCase("ebook", null)]
        [TestCase("ebook", 0)]
        public void promote_media_type_monitoring_should_set_only_requested_side_to_selected(string mediaType, int? existingMode)
        {
            var isAudiobook = mediaType == "audiobook";
            var author = new Author
            {
                Id = 6,
                Name = "Scoped Author",
                AudiobookMonitorExisting = isAudiobook ? existingMode : null,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = isAudiobook ? null : existingMode,
                EbookMonitorFuture = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                SyncMonitoredAcrossFormats = true
            };
            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { author.Id, author } });
            var service = BuildService(repository, new StubRootFolderService());

            service.PromoteMediaTypeMonitoringToSelected(author.Id, mediaType);

            Assert.Multiple(() =>
            {
                Assert.That(
                    isAudiobook
                        ? repository.LastUpdatedAuthor.AudiobookMonitorExisting
                        : repository.LastUpdatedAuthor.EbookMonitorExisting,
                    Is.EqualTo(2));
                Assert.That(
                    isAudiobook
                        ? repository.LastUpdatedAuthor.EbookMonitorExisting
                        : repository.LastUpdatedAuthor.AudiobookMonitorExisting,
                    Is.Null);
                Assert.That(repository.LastUpdatedAuthor.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(repository.LastUpdatedAuthor.Monitored, Is.True);
            });
        }

        [TestCase(1)]
        [TestCase(2)]
        public void promote_media_type_monitoring_should_preserve_all_or_selected(int existingMode)
        {
            var author = new Author
            {
                Id = 7,
                Name = "Already Configured Author",
                AudiobookMonitorExisting = existingMode,
                EbookMonitorExisting = 0
            };
            var repository = new StubAuthorRepository(new Dictionary<int, Author> { { author.Id, author } });
            var service = BuildService(repository);

            service.PromoteMediaTypeMonitoringToSelected(author.Id, "audiobook");

            Assert.Multiple(() =>
            {
                Assert.That(author.AudiobookMonitorExisting, Is.EqualTo(existingMode));
                Assert.That(author.EbookMonitorExisting, Is.EqualTo(0));
                Assert.That(repository.LastUpdatedAuthor, Is.Null);
            });
        }
    }
}
