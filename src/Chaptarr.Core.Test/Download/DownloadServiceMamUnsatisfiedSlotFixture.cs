using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadServiceMamUnsatisfiedSlotFixture
    {
        private class NoopProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var returnType = targetMethod.ReturnType;
                if (returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }

        private sealed class RecordingSlotGuard : IMamUnsatisfiedSlotGuard
        {
            private readonly List<string> _calls;

            public RecordingSlotGuard(List<string> calls, MamUnsatisfiedSlotAvailability availability)
            {
                _calls = calls;
                Availability = availability;
            }

            public MamUnsatisfiedSlotAvailability Availability { get; set; }

            public MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook)
            {
                return Availability;
            }

            public MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook)
            {
                _calls.Add("reserve");
                return Availability;
            }

            public void Reconcile(MyAnonaMouse indexer, MyAnonaMouseAccountStatus status)
            {
            }
        }

        private sealed class RecordingDownloadClient : IDownloadClient
        {
            private readonly List<string> _calls;

            public RecordingDownloadClient(List<string> calls)
            {
                _calls = calls;
                Definition = new DownloadClientDefinition { Id = 7, Name = "Client" };
            }

            public int DownloadCount { get; private set; }
            public Exception DownloadException { get; set; }
            public Exception GetItemsException { get; set; }
            public IEnumerable<DownloadClientItem> ExistingItems { get; set; } = Enumerable.Empty<DownloadClientItem>();
            public string Name => "Client";
            public Type ConfigContract => typeof(object);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public DownloadProtocol Protocol => DownloadProtocol.Torrent;

            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer)
            {
                _calls.Add("download");
                DownloadCount++;

                if (DownloadException != null)
                {
                    throw DownloadException;
                }

                return Task.FromResult("download-id");
            }

            public IEnumerable<DownloadClientItem> GetItems()
            {
                _calls.Add("get-items");
                if (GetItemsException != null)
                {
                    throw GetItemsException;
                }

                return ExistingItems;
            }
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => item;
            public void RemoveItem(DownloadClientItem item, bool deleteData) { }
            public DownloadClientInfo GetStatus() => new();
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
            public ValidationResult Test() => new();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
        }

        private sealed class SingleDownloadClientProvider : IProvideDownloadClient
        {
            private readonly IDownloadClient _client;

            public SingleDownloadClientProvider(IDownloadClient client)
            {
                _client = client;
            }

            public HashSet<int> LastRequestedTags { get; private set; }

            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null)
            {
                LastRequestedTags = tags == null ? null : new HashSet<int>(tags);
                return _client;
            }

            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false) => new[] { _client };
            public IDownloadClient Get(int id) => _client;
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> PublishedEvents { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class RecordingIndexerStatusService : IIndexerStatusService
        {
            public int FailureCount { get; private set; }
            public int SuccessCount { get; private set; }

            public List<IndexerStatus> GetBlockedProviders() => new();

            public void RecordSuccess(int providerId)
            {
                SuccessCount++;
            }

            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default)
            {
                FailureCount++;
            }

            public void RecordConnectionFailure(int providerId)
            {
            }

            public ReleaseInfo GetLastRssSyncReleaseInfo(int indexerId) => null;

            public void UpdateRssSyncStatus(int indexerId, ReleaseInfo releaseInfo)
            {
            }
        }

        [Test]
        public void should_reserve_before_the_shared_download_path_fetches_the_mam_torrent()
        {
            var calls = new List<string>();
            var client = new RecordingDownloadClient(calls);
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept());
            var service = CreateService(client, guard);

            service.DownloadReport(RemoteBook(), null).GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "reserve", "download" }));
                Assert.That(client.DownloadCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void should_never_fetch_the_mam_torrent_when_no_slot_can_be_reserved()
        {
            var calls = new List<string>();
            var client = new RecordingDownloadClient(calls);
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Reject("MAM safety pause"));
            var service = CreateService(client, guard);

            var exception = Assert.ThrowsAsync<MamUnsatisfiedSlotsUnavailableException>(async () => await service.DownloadReport(RemoteBook(), null));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("MAM safety pause"));
                Assert.That(calls, Is.EqualTo(new[] { "reserve" }));
                Assert.That(client.DownloadCount, Is.Zero);
            });
        }

        [Test]
        public void should_not_publish_a_grab_or_fail_the_indexer_when_the_download_client_rejects_the_release()
        {
            var calls = new List<string>();
            var remoteBook = RemoteBook();
            var client = new RecordingDownloadClient(calls)
            {
                DownloadException = new DownloadClientRejectedReleaseException(remoteBook.Release, "Torrent already exists")
            };
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept());
            var events = new RecordingEventAggregator();
            var indexerStatus = new RecordingIndexerStatusService();
            var service = CreateService(client, guard, events, indexerStatus);

            Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(async () => await service.DownloadReport(remoteBook, null));

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "reserve", "download" }));
                Assert.That(events.PublishedEvents.OfType<BookGrabbedEvent>(), Is.Empty);
                Assert.That(indexerStatus.FailureCount, Is.Zero);
            });
        }

        [Test]
        public void should_report_a_matching_existing_torrent_without_downloading_it_again()
        {
            var calls = new List<string>();
            var infoHash = "abcdef123456";
            var remoteBook = RemoteBook();
            remoteBook.Release = new TorrentInfo
            {
                Guid = "existing-torrent",
                DownloadProtocol = DownloadProtocol.Torrent,
                IndexerId = 0,
                InfoHash = infoHash,
                Title = "The existing release"
            };
            var client = new RecordingDownloadClient(calls)
            {
                ExistingItems = new[]
                {
                    new DownloadClientItem { DownloadId = infoHash.ToUpperInvariant(), Title = "Already queued" }
                }
            };
            var events = new RecordingEventAggregator();
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept());
            var service = CreateService(client, guard, events);

            var exception = Assert.ThrowsAsync<ExistingTorrentFoundException>(async () => await service.DownloadReport(remoteBook, null));

            Assert.Multiple(() =>
            {
                Assert.That(exception.DownloadTitle, Is.EqualTo("Already queued"));
                Assert.That(calls, Is.EqualTo(new[] { "get-items" }));
                Assert.That(client.DownloadCount, Is.Zero);
                Assert.That(events.PublishedEvents.OfType<BookGrabbedEvent>(), Is.Empty);
            });
        }

        [Test]
        public async Task should_adopt_a_matching_existing_torrent_without_downloading_it()
        {
            var calls = new List<string>();
            var infoHash = "abcdef123456";
            var remoteBook = RemoteBook();
            remoteBook.Release = new TorrentInfo
            {
                Guid = "existing-torrent",
                DownloadProtocol = DownloadProtocol.Torrent,
                IndexerId = 0,
                InfoHash = infoHash,
                Title = "The existing release"
            };
            var client = new RecordingDownloadClient(calls)
            {
                ExistingItems = new[]
                {
                    new DownloadClientItem { DownloadId = infoHash.ToUpperInvariant(), Title = "Already queued" }
                }
            };
            var events = new RecordingEventAggregator();
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept());
            var indexerStatus = new RecordingIndexerStatusService();
            var service = CreateService(client, guard, events, indexerStatus);

            await service.AdoptExistingTorrent(remoteBook, null);

            var grabbed = events.PublishedEvents.OfType<BookGrabbedEvent>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "get-items" }));
                Assert.That(client.DownloadCount, Is.Zero);
                Assert.That(grabbed.DownloadId, Is.EqualTo(infoHash.ToUpperInvariant()));
                Assert.That(indexerStatus.SuccessCount, Is.Zero);
                Assert.That(indexerStatus.FailureCount, Is.Zero);
            });
        }

        [Test]
        public void should_reject_adoption_when_the_torrent_is_no_longer_in_the_client()
        {
            var calls = new List<string>();
            var remoteBook = RemoteBook();
            remoteBook.Release = new TorrentInfo
            {
                Guid = "missing-torrent",
                DownloadProtocol = DownloadProtocol.Torrent,
                IndexerId = 0,
                InfoHash = "abcdef123456",
                Title = "The missing release"
            };
            var client = new RecordingDownloadClient(calls);
            var events = new RecordingEventAggregator();
            var service = CreateService(client, new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept()), events);

            Assert.ThrowsAsync<ExistingTorrentNotFoundException>(async () => await service.AdoptExistingTorrent(remoteBook, null));

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "get-items" }));
                Assert.That(client.DownloadCount, Is.Zero);
                Assert.That(events.PublishedEvents.OfType<BookGrabbedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void should_fail_closed_when_an_explicit_adoption_cannot_check_the_client()
        {
            var calls = new List<string>();
            var remoteBook = RemoteBook();
            remoteBook.Release = new TorrentInfo
            {
                Guid = "unavailable-torrent",
                DownloadProtocol = DownloadProtocol.Torrent,
                IndexerId = 0,
                InfoHash = "abcdef123456",
                Title = "The unavailable release"
            };
            var client = new RecordingDownloadClient(calls)
            {
                GetItemsException = new DownloadClientException("Client unavailable")
            };
            var events = new RecordingEventAggregator();
            var service = CreateService(client, new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept()), events);

            Assert.ThrowsAsync<DownloadClientException>(async () => await service.AdoptExistingTorrent(remoteBook, null));

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "get-items" }));
                Assert.That(client.DownloadCount, Is.Zero);
                Assert.That(events.PublishedEvents.OfType<BookGrabbedEvent>(), Is.Empty);
            });
        }

        [Test]
        public async Task normal_download_should_continue_when_duplicate_lookup_fails()
        {
            var calls = new List<string>();
            var remoteBook = RemoteBook();
            remoteBook.Release = new TorrentInfo
            {
                Guid = "lookup-failure-torrent",
                DownloadProtocol = DownloadProtocol.Torrent,
                IndexerId = 0,
                InfoHash = "abcdef123456",
                Title = "The lookup-failure release"
            };
            var client = new RecordingDownloadClient(calls)
            {
                GetItemsException = new DownloadClientException("Client unavailable")
            };
            var guard = new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept());
            var events = new RecordingEventAggregator();
            var service = CreateService(client, guard, events);

            await service.DownloadReport(remoteBook, null);

            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "get-items", "reserve", "download" }));
                Assert.That(client.DownloadCount, Is.EqualTo(1));
                Assert.That(events.PublishedEvents.OfType<BookGrabbedEvent>().Count(), Is.EqualTo(1));
            });
        }

        [TestCase(false, 101)]
        [TestCase(true, 7)]
        public async Task download_client_selection_should_preserve_source_author_tags_for_migrated_profiles(bool bookshelfCompatibility, int expectedTag)
        {
            var calls = new List<string>();
            var remoteBook = RemoteBook();
            remoteBook.Author.Tags = new HashSet<int> { 7 };
            remoteBook.Author.AudiobookTags = new HashSet<int> { 101 };
            remoteBook.Author.EbookTags = new HashSet<int> { 201 };
            var client = new RecordingDownloadClient(calls);
            var provider = new SingleDownloadClientProvider(client);
            var service = CreateService(client, new RecordingSlotGuard(calls, MamUnsatisfiedSlotAvailability.Accept()),
                provider: provider, bookshelfCompatibility: bookshelfCompatibility);

            await service.DownloadReport(remoteBook, null);

            Assert.That(provider.LastRequestedTags, Is.EquivalentTo(new[] { expectedTag }));
        }

        private static DownloadService CreateService(IDownloadClient client,
                                                      IMamUnsatisfiedSlotGuard guard,
                                                      IEventAggregator eventAggregator = null,
                                                      IIndexerStatusService indexerStatusService = null,
                                                      SingleDownloadClientProvider provider = null,
                                                      bool bookshelfCompatibility = false)
        {
            return new DownloadService(
                provider ?? new SingleDownloadClientProvider(client),
                Noop<IDownloadClientStatusService>(),
                Noop<IIndexerFactory>(),
                indexerStatusService ?? Noop<IIndexerStatusService>(),
                Noop<IRateLimitService>(),
                eventAggregator ?? Noop<IEventAggregator>(),
                Noop<ISeedConfigProvider>(),
                guard,
                ConfigServiceTestProxy.Create(bookshelfMatchCompatibility: bookshelfCompatibility),
                LogManager.GetCurrentClassLogger());
        }

        private static RemoteBook RemoteBook()
        {
            return new RemoteBook
            {
                Author = new Author { Id = 1, Name = "Author" },
                Books = new List<Book>
                {
                    new Book { Id = 2, Title = "Book", MediaType = BookMediaType.Audiobook }
                },
                Release = new ReleaseInfo
                {
                    Guid = "MAM-659145",
                    Indexer = "MAM",
                    DownloadProtocol = DownloadProtocol.Torrent,
                    Title = "Author - Book"
                },
                ParsedBookInfo = new ParsedBookInfo()
            };
        }

        private static T Noop<T>() where T : class
        {
            return DispatchProxy.Create<T, NoopProxy>();
        }
    }
}
