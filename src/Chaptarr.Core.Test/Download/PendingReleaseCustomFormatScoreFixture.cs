using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseCustomFormatScoreFixture
    {
        [Test]
        public void restart_hydration_should_restore_the_direct_custom_format_score()
        {
            var format = new CustomFormat
            {
                Id = 10,
                Name = "Preferred Narrators",
                AppliesTo = CustomFormatMediaType.Audiobook
            };
            var profile = new QualityProfile
            {
                Id = 20,
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = 50 }
                }
            };
            var author = new Author
            {
                Id = 30,
                Name = "Test Author",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(profile)
            };
            var book = new Book
            {
                Id = 40,
                AuthorId = author.Id,
                Title = "Test Book",
                MediaType = BookMediaType.Audiobook
            };
            var pending = new PendingRelease
            {
                Id = 50,
                AuthorId = author.Id,
                AdditionalInfo = new PendingReleaseAdditionalInfo
                {
                    ReleaseSource = ReleaseSourceType.InteractiveSearch
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    ReleaseTitle = "Test Author - Test Book",
                    Quality = new QualityModel(Quality.M4B)
                },
                Release = new ReleaseInfo
                {
                    Title = "Test Author - Test Book",
                    Size = 1234
                }
            };

            var repository = DispatchProxy.Create<IPendingReleaseRepository, PendingRepositoryProxy>();
            ((PendingRepositoryProxy)(object)repository).Releases = new List<PendingRelease> { pending };
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author> { author };
            var parsingService = DispatchProxy.Create<IParsingService, ParsingServiceProxy>();
            ((ParsingServiceProxy)(object)parsingService).Books = new List<Book> { book };
            var aggregationService = DispatchProxy.Create<IRemoteBookAggregationService, DefaultProxy<IRemoteBookAggregationService>>();
            var calculator = new FixedCustomFormatCalculationService(format);
            var service = new PendingReleaseService(
                indexerStatusService: null,
                repository,
                authorService,
                parsingService,
                delayProfileService: null,
                taskManager: null,
                configService: null,
                calculator,
                aggregationService,
                downloadClientFactory: null,
                indexerFactory: null,
                eventAggregator: null,
                LogManager.GetCurrentClassLogger());

            var remoteBook = service.GetPendingRemoteBooks(author.Id).Single();

            Assert.That(remoteBook.CustomFormats, Is.EqualTo(new[] { format }));
            Assert.That(remoteBook.CustomFormatScore, Is.EqualTo(50));
            Assert.That(remoteBook.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
        }

        [Test]
        public void should_restore_bookshelf_pending_audiobook_quality_and_legacy_protocol_order_for_migrated_installs()
        {
            var queue = GetBestPendingQueueItem(BookMediaType.Audiobook, bookshelfCompatibility: true);

            Assert.That(queue.Title, Is.EqualTo("Legacy preferred protocol"));
        }

        [Test]
        public void should_keep_chaptarr_pending_audiobook_custom_format_order_for_fresh_installs()
        {
            var queue = GetBestPendingQueueItem(BookMediaType.Audiobook, bookshelfCompatibility: false);

            Assert.That(queue.Title, Is.EqualTo("Preferred custom format"));
        }

        [Test]
        public void should_keep_chaptarr_pending_ebook_order_when_migration_compatibility_is_enabled()
        {
            var queue = GetBestPendingQueueItem(BookMediaType.Ebook, bookshelfCompatibility: true);

            Assert.That(queue.Title, Is.EqualTo("Preferred custom format"));
        }

        private static NzbDrone.Core.Queue.Queue GetBestPendingQueueItem(BookMediaType mediaType, bool bookshelfCompatibility)
        {
            var quality = mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB;
            var profile = new QualityProfile
            {
                Id = 20,
                Name = mediaType == BookMediaType.Audiobook ? "Audiobook" : "Ebook",
                ProfileType = mediaType == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook,
                PreferCustomFormatsOverQuality = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = quality, Allowed = true }
                }
            };
            var preferredFormat = new CustomFormat
            {
                Id = 10,
                Name = "Preferred",
                AppliesTo = mediaType == BookMediaType.Audiobook ? CustomFormatMediaType.Audiobook : CustomFormatMediaType.Ebook
            };
            profile.FormatItems = new List<ProfileFormatItem>
            {
                new() { Format = preferredFormat, Score = 50 }
            };
            var author = new Author
            {
                Id = 30,
                Name = "Test Author",
                Tags = new HashSet<int> { 1 },
                AudiobookTags = new HashSet<int> { 2 },
                EbookTags = new HashSet<int> { 3 },
                AudiobookQualityProfileId = profile.Id,
                EbookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile,
                EbookQualityProfile = profile
            };
            var book = new Book
            {
                Id = 40,
                AuthorId = author.Id,
                Author = author,
                Title = "Test Book",
                MediaType = mediaType
            };
            var pendingReleases = new List<PendingRelease>
            {
                CreatePendingRelease(author, quality, "Legacy preferred protocol", DownloadProtocol.Usenet, 50),
                CreatePendingRelease(author, quality, "Preferred custom format", DownloadProtocol.Torrent, 51)
            };

            var repository = DispatchProxy.Create<IPendingReleaseRepository, PendingRepositoryProxy>();
            ((PendingRepositoryProxy)(object)repository).Releases = pendingReleases;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author> { author };
            var parsingService = DispatchProxy.Create<IParsingService, ParsingServiceProxy>();
            ((ParsingServiceProxy)(object)parsingService).Books = new List<Book> { book };
            var aggregationService = DispatchProxy.Create<IRemoteBookAggregationService, DefaultProxy<IRemoteBookAggregationService>>();
            var configService = Chaptarr.Core.Test.ConfigServiceTestProxy.Create(bookshelfMatchCompatibility: bookshelfCompatibility);
            var service = new PendingReleaseService(
                indexerStatusService: null,
                repository,
                authorService,
                parsingService,
                new DelayProfileServiceStub(),
                DispatchProxy.Create<ITaskManager, DefaultProxy<ITaskManager>>(),
                configService,
                new ReleaseCustomFormatCalculationService(preferredFormat),
                aggregationService,
                DispatchProxy.Create<IDownloadClientFactory, DefaultProxy<IDownloadClientFactory>>(),
                DispatchProxy.Create<IIndexerFactory, DefaultProxy<IIndexerFactory>>(),
                DispatchProxy.Create<IEventAggregator, DefaultProxy<IEventAggregator>>(),
                LogManager.GetCurrentClassLogger());

            return service.GetPendingQueue().Single();
        }

        private static PendingRelease CreatePendingRelease(Author author, Quality quality, string title, DownloadProtocol protocol, int id)
        {
            return new PendingRelease
            {
                Id = id,
                AuthorId = author.Id,
                Title = title,
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    ReleaseTitle = title,
                    Quality = new QualityModel(quality)
                },
                Release = new ReleaseInfo
                {
                    Title = title,
                    Size = 1024,
                    Indexer = "Test indexer",
                    DownloadProtocol = protocol,
                    PublishDate = DateTime.UtcNow.AddMinutes(-1)
                }
            };
        }

        private class PendingRepositoryProxy : DispatchProxy
        {
            public List<PendingRelease> Releases { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IPendingReleaseRepository.AllByAuthorId) ||
                    targetMethod?.Name == nameof(IPendingReleaseRepository.WithoutFallback))
                {
                    return targetMethod.Name == nameof(IPendingReleaseRepository.AllByAuthorId)
                        ? Releases.Where(release => release.AuthorId == (int)args[0]).ToList()
                        : Releases;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Authors.Where(author => ids.Contains(author.Id)).ToList();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class ParsingServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IParsingService.GetBooks))
                {
                    return Books;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DefaultProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.ReturnType == typeof(void)
                    ? null
                    : targetMethod?.ReturnType.IsValueType == true
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
            }
        }

        private sealed class FixedCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly List<CustomFormat> _formats;

            public FixedCustomFormatCalculationService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => _formats;
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => throw new NotImplementedException();
        }

        private sealed class ReleaseCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly CustomFormat _preferredFormat;

            public ReleaseCustomFormatCalculationService(CustomFormat preferredFormat)
            {
                _preferredFormat = preferredFormat;
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size)
            {
                return remoteBook.Release.Title == "Preferred custom format"
                    ? new List<CustomFormat> { _preferredFormat }
                    : new List<CustomFormat>();
            }

            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => throw new NotImplementedException();
        }

        private sealed class DelayProfileServiceStub : IDelayProfileService
        {
            private static DelayProfile GetProfile(HashSet<int> tags)
            {
                var preferredProtocol = tags?.Contains(1) == true || tags?.Contains(3) == true
                    ? DownloadProtocol.Usenet
                    : DownloadProtocol.Torrent;

                return new DelayProfile
                {
                    PreferredProtocol = preferredProtocol,
                    UsenetDelay = 0,
                    TorrentDelay = 0
                };
            }

            public DelayProfile BestForTags(HashSet<int> tags) => GetProfile(tags);
            public List<DelayProfile> AllForTags(HashSet<int> tags) => new() { GetProfile(tags) };
            public DelayProfile Add(DelayProfile profile) => throw new NotImplementedException();
            public DelayProfile Update(DelayProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<DelayProfile> All() => throw new NotImplementedException();
            public DelayProfile Get(int id) => throw new NotImplementedException();
            public List<DelayProfile> AllForTag(int tagId) => throw new NotImplementedException();
            public List<DelayProfile> Reorder(int id, int? afterId) => throw new NotImplementedException();
        }
    }
}
