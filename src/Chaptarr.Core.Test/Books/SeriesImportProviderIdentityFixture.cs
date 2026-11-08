using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    /// <summary>
    /// Author.Id is a local database key. It shares a numeric space with every provider's author
    /// ids, so sending it upstream asks the metadata server for whichever author happens to carry
    /// the same digits. An author with no provider identity has nothing to look up.
    /// </summary>
    [TestFixture]
    public class SeriesImportProviderIdentityFixture
    {
        private AuthorInfoProxy _authorInfo;
        private Author _author;
        private RefreshSeriesProxy _refreshSeries;
        private SeriesServiceProxy _seriesService;
        private EventAggregatorProxy _eventAggregator;

        [SetUp]
        public void SetUp()
        {
            _author = new Author
            {
                Id = 2514,
                Name = "Suzanne Forster"
            };
        }

        [Test]
        public void should_not_request_author_info_for_an_author_without_provider_identity()
        {
            var subject = BuildSubject();

            subject.ProcessSeriesForAuthor(_author.Id);

            Assert.That(_authorInfo.RequestedIds, Is.Empty,
                "an author with no provider identity has nothing to look up upstream");
        }

        [Test]
        public void should_request_author_info_by_provider_id_when_one_is_present()
        {
            _author.GoodreadsAuthorId = "gr:2514";
            var subject = BuildSubject();

            subject.ProcessSeriesForAuthor(_author.Id);

            Assert.That(_authorInfo.RequestedIds, Is.EqualTo(new[] { "gr:2514" }));
        }

        private SeriesImportService BuildSubject()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author> { _author };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book>
            {
                new Book { Id = 1, AuthorId = _author.Id, Title = "Shameless" }
            };

            var seriesService = DispatchProxy.Create<ISeriesService, SeriesServiceProxy>();
            _seriesService = (SeriesServiceProxy)(object)seriesService;
            var authorInfo = DispatchProxy.Create<IProvideAuthorInfo, AuthorInfoProxy>();
            _authorInfo = (AuthorInfoProxy)(object)authorInfo;
            var refreshSeries = DispatchProxy.Create<IRefreshSeriesService, RefreshSeriesProxy>();
            _refreshSeries = (RefreshSeriesProxy)(object)refreshSeries;
            var eventAggregator = DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>();
            _eventAggregator = (EventAggregatorProxy)(object)eventAggregator;

            return new SeriesImportService(
                authorService,
                seriesService,
                bookService,
                refreshSeries,
                authorInfo,
                eventAggregator,
                LogManager.GetLogger("SeriesImportProviderIdentityFixture"));
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetExistingAuthors))
                {
                    return Authors;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        public class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return Books;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        public class SeriesServiceProxy : DispatchProxy
        {
            public List<int> RequestedAuthorIds { get; } = new();
            public List<List<Series>> Responses { get; } = new() { new List<Series>() };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ISeriesService.GetByAuthorId))
                {
                    RequestedAuthorIds.Add((int)args[0]);
                    return Responses[Math.Min(RequestedAuthorIds.Count - 1, Responses.Count - 1)];
                }

                throw new NotImplementedException($"Test proxy does not implement ISeriesService.{targetMethod?.Name}");
            }
        }

        public class EventAggregatorProxy : DispatchProxy
        {
            public List<object> PublishedEvents { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEventAggregator.PublishEvent))
                {
                    PublishedEvents.Add(args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IEventAggregator.{targetMethod?.Name}");
            }
        }

        public class AuthorInfoProxy : DispatchProxy
        {
            public List<string> RequestedIds { get; } = new();
            public Author Response { get; set; } = new Author { Series = new List<Series>() };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IProvideAuthorInfo.GetAuthorInfo))
                {
                    RequestedIds.Add((string)args[0]);
                    return Response;
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideAuthorInfo.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_publish_refreshed_series_from_one_snapshot_without_mutating_cached_author()
        {
            _author.GoodreadsAuthorId = "gr:2514";
            var cachedSeries = new List<Series> { new Series { Id = 99, Title = "Cached series" } };
            _author.Series = cachedSeries;
            var subject = BuildSubject();
            var refreshedSeries = new List<Series>
            {
                new Series { Id = 1, Title = "First refreshed series" },
                new Series { Id = 2, Title = "Second refreshed series" }
            };
            _seriesService.Responses.Add(refreshedSeries);
            _seriesService.Responses.Add(refreshedSeries);
            _authorInfo.Response = new Author
            {
                Series = new List<Series> { new Series { Title = "Remote series", GoodreadsSeriesId = "gr:1" } }
            };

            subject.ProcessSeriesForAuthor(_author.Id);

            Assert.Multiple(() =>
            {
                Assert.That(_seriesService.RequestedAuthorIds, Is.EqualTo(new[] { _author.Id, _author.Id }));
                Assert.That(_eventAggregator.PublishedEvents, Has.Count.EqualTo(2));
                Assert.That(((SeriesRefreshCompleteEvent)_eventAggregator.PublishedEvents[0]).Series, Is.SameAs(refreshedSeries[0]));
                Assert.That(((SeriesRefreshCompleteEvent)_eventAggregator.PublishedEvents[1]).Series, Is.SameAs(refreshedSeries[1]));
                Assert.That(_author.Series, Is.SameAs(cachedSeries));
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task event_must_use_only_an_explicit_remote_snapshot(bool includeSnapshot)
        {
            _author.GoodreadsAuthorId = "gr:2514";
            _author.Series = new List<Series> { new Series { Id = 99, Title = "Local series" } };
            var subject = BuildSubject();
            var remote = new Author { Series = new List<Series> { new Series { Title = "Authoritative series", GoodreadsSeriesId = "gr:1",
                SeriesBooks = new List<SeriesBook> { new SeriesBook { BookId = "hc:1", Position = "1" } } } } };
            _authorInfo.Response = remote;

            subject.Handle(new AuthorRefreshCompleteEvent(_author, includeSnapshot ? remote : null));

            var received = await _refreshSeries.Received.Task.WaitAsync(System.TimeSpan.FromSeconds(5));
            Assert.That(received, Is.SameAs(remote));
            Assert.That(_authorInfo.RequestedIds.Count, Is.EqualTo(includeSnapshot ? 0 : 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task triggers_during_import_apply_the_newest_snapshot_even_after_a_failure(bool firstFails)
        {
            var subject = BuildSubject();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var applied = new System.Collections.Concurrent.ConcurrentQueue<Author>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new Author { Name = "First", Series = new List<Series> { new Series { GoodreadsSeriesId = "gr:1" } } };
            var middle = new Author { Name = "Middle", Series = first.Series };
            var latest = new Author { Name = "Latest", Series = first.Series };
            _refreshSeries.OnRefresh = snapshot =>
            {
                applied.Enqueue(snapshot);
                if (ReferenceEquals(snapshot, first))
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release import.");
                    if (firstFails) throw new InvalidOperationException("Fixture series failure");
                }
                else
                {
                    completed.TrySetResult();
                }
            };
            try
            {
                // Events are synchronous during author refresh. Scheduling must return without
                // trying to take this author lock on another thread and waiting for its worker.
                lock (BookService.GetWantedEditionLock(_author.Id))
                {
                    subject.Handle(new AuthorRefreshCompleteEvent(_author, first));
                }

                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                subject.Handle(new AuthorRefreshCompleteEvent(_author, middle));
                subject.Handle(new AuthorRefreshCompleteEvent(_author, latest));
            }
            finally
            {
                release.Set();
            }

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(applied, Is.EqualTo(new[] { first, latest }));
            Assert.That(_authorInfo.RequestedIds, Is.Empty, "Snapshots avoid another metadata-server request.");
        }

        public class RefreshSeriesProxy : DispatchProxy
        {
            public TaskCompletionSource<Author> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Action<Author> OnRefresh { get; set; }
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRefreshSeriesService.RefreshSeriesInfo))
                {
                    OnRefresh?.Invoke((Author)args[2]);
                    Received.TrySetResult((Author)args[2]);
                    return false;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

    }
}
