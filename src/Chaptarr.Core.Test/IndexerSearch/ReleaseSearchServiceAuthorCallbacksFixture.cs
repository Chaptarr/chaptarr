using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public class ReleaseSearchServiceAuthorCallbacksFixture
    {
        [Test]
        public async Task author_search_should_publish_ordered_roster_and_book_scoped_results()
        {
            var author = BuildAuthor();
            var first = BuildBook(101, BookMediaType.Audiobook, author, new DateTime(2026, 1, 1));
            var empty = BuildBook(102, BookMediaType.Audiobook, author, new DateTime(2026, 1, 2));
            var third = BuildBook(103, BookMediaType.Audiobook, author, new DateTime(2026, 1, 3));
            var otherMedia = BuildBook(104, BookMediaType.Ebook, author, new DateTime(2025, 1, 1));
            var harness = BuildHarness(new List<Book> { empty, otherMedia, third, first }, author);
            var fetchedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(bookId == empty.Id
                    ? new List<ReleaseInfo>()
                    : new List<ReleaseInfo> { BuildRelease("shared-guid"), BuildRelease("shared-guid") });
            };

            IReadOnlyList<Book> roster = null;
            var completed = new List<(int BookId, List<DownloadDecision> Decisions, Exception Exception)>();
            await harness.Subject.AuthorSearch(
                author.Id,
                false,
                true,
                false,
                BookMediaType.Audiobook,
                books =>
                {
                    roster = books;
                    return Task.CompletedTask;
                },
                (book, decisions, exception) =>
                {
                    completed.Add((book.Id, decisions.Decisions, exception));
                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(roster.Select(book => book.Id), Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(completed.Select(item => item.BookId), Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(completed[0].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed[1].Decisions, Is.Empty);
                Assert.That(completed[2].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed.All(item => item.Exception == null), Is.True);
                Assert.That(completed[0].Decisions.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(completed[2].Decisions.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(1 + roster.Count), "Each automatic book search rechecks the cached author; the catalog is loaded only once.");
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.AuthorCatalog.Count), Is.All.EqualTo(3));
                Assert.That(harness.Indexer.Searches.All(criteria => criteria.AuthorCatalog.All(book => book.MediaType == BookMediaType.Audiobook)), Is.True);
            });
        }

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public async Task author_search_should_use_current_catalog_when_cached_author_books_are_stale(
            BookMediaType mediaType,
            bool staleRosterOvercounts)
        {
            var author = BuildAuthor();
            var otherMediaType = mediaType == BookMediaType.Audiobook
                ? BookMediaType.Ebook
                : BookMediaType.Audiobook;
            var currentCatalog = Enumerable.Range(0, 6)
                .Select(index => BuildBook(301 + index, mediaType, author, new DateTime(2026, 1, 1).AddDays(index)))
                .ToList();

            foreach (var (book, index) in currentCatalog.Select((book, index) => (book, index)))
            {
                SetMonitored(book, mediaType, staleRosterOvercounts ? index == 0 : true);
            }

            var currentOtherMediaBook = BuildBook(399, otherMediaType, author, new DateTime(2026, 1, 10));
            currentCatalog.Add(currentOtherMediaBook);

            var cachedRoster = currentCatalog
                .Where(book => book.MediaType == mediaType)
                .Select(book => CloneBookWithMonitoring(book, author, monitored: staleRosterOvercounts || book.Id == currentCatalog[0].Id))
                .ToList();
            cachedRoster.Add(CloneBookWithMonitoring(currentOtherMediaBook, author, monitored: true));
            author.Books = cachedRoster;

            var harness = BuildHarness(currentCatalog, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

            IReadOnlyList<Book> roster = null;
            var completedBookIds = new List<int>();
            await harness.Subject.AuthorSearch(
                author.Id,
                false,
                true,
                false,
                mediaType,
                books =>
                {
                    roster = books;
                    return Task.CompletedTask;
                },
                (book, _, _) =>
                {
                    completedBookIds.Add(book.Id);
                    return Task.CompletedTask;
                });

            var expectedBookIds = currentCatalog
                .Where(book => book.MediaType == mediaType && book.IsMonitoredWithAuthor())
                .Select(book => book.Id)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(roster.Select(book => book.Id).ToArray(), Is.EqualTo(expectedBookIds));
                Assert.That(completedBookIds, Is.EqualTo(expectedBookIds));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id).ToArray(), Is.EqualTo(expectedBookIds));
                Assert.That(harness.Indexer.Searches.All(criteria => criteria.AuthorCatalog.All(book => book.MediaType == mediaType)), Is.True);
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(1 + expectedBookIds.Length), "Each automatic book search rechecks the cached author; the catalog is loaded only once.");
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public async Task single_book_search_should_load_sibling_evidence_when_author_books_are_stale(BookMediaType mediaType)
        {
            var author = BuildAuthor();
            var target = BuildBook(501, mediaType, author, new DateTime(2026, 1, 1));
            var currentSibling = BuildBook(502, mediaType, author, new DateTime(2026, 1, 2));
            var currentCatalog = new List<Book> { target, currentSibling };
            var staleSibling = BuildBook(503, mediaType, author, new DateTime(2026, 1, 3));
            author.Books = new List<Book> { target, staleSibling };

            var harness = BuildHarness(currentCatalog, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

            await harness.Subject.BookSearch(target, false, true, false);

            Assert.Multiple(() =>
            {
                Assert.That(harness.Indexer.Searches, Has.Count.EqualTo(1));
                Assert.That(harness.Indexer.Searches.Single().Books.Select(book => book.Id).ToArray(), Is.EqualTo(new[] { target.Id }));
                Assert.That(harness.Indexer.Searches.Single().AuthorCatalog.Select(book => book.Id).ToArray(), Is.EqualTo(new[] { target.Id, currentSibling.Id }));
                Assert.That(harness.Indexer.Searches.Single().AuthorCatalog.All(book => book.MediaType == mediaType), Is.True);
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task grouped_author_search_should_publish_fresh_roster_when_cached_author_books_understate_monitoring()
        {
            var author = BuildAuthor();
            var currentCatalog = Enumerable.Range(0, 6)
                .Select(index => BuildBook(401 + index, BookMediaType.Audiobook, author, new DateTime(2026, 1, 1).AddDays(index)))
                .ToList();
            var staleRoster = currentCatalog
                .Select((book, index) => CloneBookWithMonitoring(book, author, monitored: index == 0))
                .ToList();
            author.Books = staleRoster;

            var harness = BuildHarness(currentCatalog, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

            var cache = new CacheManager();
            var prioritize = DispatchProxy.Create<IPrioritizeDownloadDecision, SearchPrioritizeProxy>();
            var broadcaster = new SearchBroadcaster();
            var driver = new GroupedAuthorSearchDriver(harness.Subject, prioritize, broadcaster, cache, autoStart: true);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddCors(options => options.AddPolicy("ApiCorsPolicy", policy => policy.AllowAnyOrigin()));
            builder.Services.AddControllers().AddApplicationPart(typeof(ReleaseController).Assembly).AddControllersAsServices();
            builder.Services.AddTransient(_ => new ReleaseController(null, harness.Subject, null, prioritize,
                null, null, null, null, null, cache,
                LogManager.GetLogger(nameof(ReleaseSearchServiceAuthorCallbacksFixture)), driver.Service));
            await using var app = builder.Build();
            app.UseCors();
            app.MapControllers();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(20),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var searchId = Guid.NewGuid().ToString();

            using var result = await client.GetAsync($"/api/v1/release?authorId={author.Id}&mediaType=audiobook&searchId={searchId}");
            result.EnsureSuccessStatusCode();
            var admission = await result.Content.ReadFromJsonAsync<AuthorReleaseSearchResponse>();
            Assert.That(admission.IsComplete, Is.False);
            await driver.Executions[searchId].WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broadcaster.LastResult.BookResults, Is.Empty, "Completion is a compact notification; results are fetched from the saved search");
            var response = await client.GetFromJsonAsync<AuthorReleaseSearchResponse>(
                $"/api/v1/release?authorId={author.Id}&mediaType=audiobook&existing=true");
            Assert.That(response.SearchId, Is.EqualTo(searchId));
            var expectedBookIds = currentCatalog.Select(book => book.Id).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(response.IsComplete, Is.True);
                Assert.That(response.BookResults.Select(group => group.BookId).ToArray(), Is.EqualTo(expectedBookIds));
                Assert.That(response.BookResults.Select(group => group.Status).ToArray(), Is.All.EqualTo("complete"));
                Assert.That(broadcaster.FirstPublishedBookResultCount, Is.EqualTo(expectedBookIds.Length));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id), Is.EqualTo(expectedBookIds));
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task legacy_author_search_should_keep_its_flat_deduped_result()
        {
            var author = BuildAuthor();
            var first = BuildBook(151, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(152, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>
            {
                BuildRelease("shared-guid")
            });

            var result = await harness.Subject.AuthorSearch(author.Id, false, true, false, BookMediaType.Audiobook, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(3), "Each automatic book search rechecks the cached author; the catalog is loaded only once.");
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task author_search_should_publish_first_book_before_second_book_finishes()
        {
            var author = BuildAuthor();
            var first = BuildBook(201, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(202, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var firstCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedBookIds = new List<int>();
            harness.Indexer.FetchHandler = async criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                if (bookId == second.Id)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task;
                }

                return new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") };
            };

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completedBookIds.Add(book.Id);
                    if (book.Id == first.Id)
                    {
                        firstCompleted.TrySetResult(true);
                    }

                    return Task.CompletedTask;
                });

            try
            {
                await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Multiple(() =>
                {
                    Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id }));
                    Assert.That(search.IsCompleted, Is.False);
                });
            }
            finally
            {
                releaseSecond.TrySetResult(true);
            }

            await search;
            Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id, second.Id }));
        }

        [Test]
        public void author_search_should_stop_at_the_next_book_when_cancelled_after_a_completion()
        {
            var author = BuildAuthor();
            var first = BuildBook(301, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(302, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            using var cancellation = new CancellationTokenSource();
            var fetchedBookIds = new List<int>();
            var completedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") });
            };

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completedBookIds.Add(book.Id);
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                cancellation.Token);

            Assert.ThrowsAsync<OperationCanceledException>(async () => await search);
            Assert.Multiple(() =>
            {
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id }));
                Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id }));
            });
        }

        [Test]
        public async Task author_search_should_report_a_book_failure_and_continue_to_the_next_book()
        {
            var author = BuildAuthor();
            var first = BuildBook(401, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(402, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease("guid") });
            harness.DecisionMaker.DecisionHandler = (reports, criteria) =>
            {
                if (criteria.Books.Single().Id == first.Id)
                {
                    throw new InvalidOperationException("first book decision failed");
                }

                return BuildDecisions(reports);
            };
            var completed = new List<(int BookId, List<DownloadDecision> Decisions, Exception Exception)>();

            await harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completed.Add((book.Id, decisions.Decisions, exception));
                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(completed.Select(item => item.BookId), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(completed[0].Decisions, Is.Empty);
                Assert.That(completed[0].Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(completed[1].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed[1].Exception, Is.Null);
            });
        }

        [Test]
        public async Task author_search_should_report_completion_callback_failure_and_continue_to_the_next_book()
        {
            var author = BuildAuthor();
            var first = BuildBook(501, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(502, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var fetchedBookIds = new List<int>();
            var callbackFailure = new InvalidOperationException("completion callback failed");
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") });
            };
            var callbackResults = new List<(int BookId, int DecisionCount, Exception Exception)>();

            await harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    callbackResults.Add((book.Id, decisions.Decisions.Count, exception));
                    if (book.Id == first.Id && exception == null)
                    {
                        return Task.FromException(callbackFailure);
                    }

                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(callbackResults.Select(result => result.BookId), Is.EqualTo(new[] { first.Id, first.Id, second.Id }));
                Assert.That(callbackResults[0].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[0].Exception, Is.Null);
                Assert.That(callbackResults[1].DecisionCount, Is.Zero);
                Assert.That(callbackResults[1].Exception, Is.SameAs(callbackFailure));
                Assert.That(callbackResults[2].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[2].Exception, Is.Null);
            });
        }

        [Test]
        public void author_search_should_stop_when_the_error_result_callback_also_fails()
        {
            var author = BuildAuthor();
            var first = BuildBook(511, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(512, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var fetchedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                fetchedBookIds.Add(criteria.Books.Single().Id);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease("guid") });
            };
            var callbackFailure = new InvalidOperationException("completion callback failed");
            var errorCallbackFailure = new InvalidOperationException("failed result callback failed");
            var callbackResults = new List<(int BookId, int DecisionCount, Exception Exception)>();

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    callbackResults.Add((book.Id, decisions.Decisions.Count, exception));
                    return Task.FromException(exception == null ? callbackFailure : errorCallbackFailure);
                });

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await search);
            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.SameAs(errorCallbackFailure));
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id }));
                Assert.That(callbackResults.Select(result => result.BookId), Is.EqualTo(new[] { first.Id, first.Id }));
                Assert.That(callbackResults[0].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[0].Exception, Is.Null);
                Assert.That(callbackResults[1].DecisionCount, Is.Zero);
                Assert.That(callbackResults[1].Exception, Is.SameAs(callbackFailure));
            });
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public async Task short_http_admission_should_survive_proxy_deadline_and_preserve_explicit_stop(bool hasPendingBook, bool requestStop)
        {
            var author = BuildAuthor();
            var first = BuildBook(701, BookMediaType.Audiobook, author, DateTime.MinValue);
            var active = BuildBook(702, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var pending = BuildBook(703, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(2));
            var harness = BuildHarness(hasPendingBook ? new List<Book> { first, active, pending } : new List<Book> { first, active }, author);
            var activeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishActive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Indexer.FetchHandler = async criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                if (bookId == active.Id)
                {
                    activeStarted.TrySetResult(true);
                    await finishActive.Task;
                }

                return new List<ReleaseInfo> { BuildRelease($"retained-{bookId}") };
            };
            harness.DecisionMaker.DecisionHandler = (reports, criteria) => reports.Select(report =>
                new DownloadDecision(new RemoteBook
                {
                    Release = report,
                    Author = criteria.Author,
                    Books = criteria.Books,
                    DownloadAllowed = true,
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) }
                })).ToList();

            var cache = new CacheManager();
            var prioritize = DispatchProxy.Create<IPrioritizeDownloadDecision, SearchPrioritizeProxy>();
            var broadcaster = new SearchBroadcaster();
            var driver = new GroupedAuthorSearchDriver(harness.Subject, prioritize, broadcaster, cache, autoStart: true);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddCors(options => options.AddPolicy("ApiCorsPolicy", policy => policy.AllowAnyOrigin()));
            builder.Services.AddControllers().AddApplicationPart(typeof(ReleaseController).Assembly).AddControllersAsServices();
            builder.Services.AddTransient(_ => new ReleaseController(null, harness.Subject, null, prioritize,
                null, null, null, null, null, cache,
                LogManager.GetLogger(nameof(ReleaseSearchServiceAuthorCallbacksFixture)), driver.Service));
            await using var app = builder.Build();
            app.UseCors();
            app.MapControllers();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var upstream = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var proxyBuilder = WebApplication.CreateSlimBuilder();
            proxyBuilder.Logging.ClearProviders();
            proxyBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var proxy = proxyBuilder.Build();
            proxy.Run(async context =>
            {
                try
                {
                    using var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.Method), address + context.Request.Path + context.Request.QueryString);
                    using var received = await upstream.SendAsync(forwarded, context.RequestAborted);
                    context.Response.StatusCode = (int)received.StatusCode;
                    context.Response.ContentType = "application/json";
                    await received.Content.CopyToAsync(context.Response.Body);
                }
                catch (OperationCanceledException)
                {
                    context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                }
            });
            await proxy.StartAsync();
            var proxyAddress = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(proxyAddress),
                Timeout = TimeSpan.FromSeconds(20),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var searchId = Guid.NewGuid().ToString();
            var search = client.GetAsync($"/api/v1/release?authorId={author.Id}&mediaType=audiobook&searchId={searchId}");

            try
            {
                await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var admitted = await search;
                Assert.That(admitted.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Admission must finish while the indexer is blocked, before the proxy returns 504.");
                Assert.That(finishActive.Task.IsCompleted, Is.False);
                if (requestStop)
                {
                    using var stop = await client.DeleteAsync($"/api/v1/release?searchId={searchId}");
                    stop.EnsureSuccessStatusCode();
                    Assert.That((await stop.Content.ReadFromJsonAsync<AuthorReleaseSearchStopResponse>()).IsStopping, Is.True);
                }

                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id),
                    Is.EqualTo(new[] { first.Id, active.Id }));
            }
            finally
            {
                finishActive.TrySetResult(true);
            }

            using var result = await search;
            result.EnsureSuccessStatusCode();
            var admission = await result.Content.ReadFromJsonAsync<AuthorReleaseSearchResponse>();
            Assert.That(admission.IsComplete, Is.False);
            await driver.Executions[searchId].WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(broadcaster.LastResult.BookResults, Is.Empty, "Completion is a compact notification; results are fetched from the saved search");
            var response = await client.GetFromJsonAsync<AuthorReleaseSearchResponse>(
                $"/api/v1/release?authorId={author.Id}&mediaType=audiobook&existing=true");
            Assert.That(response.SearchId, Is.EqualTo(searchId));
            Assert.Multiple(() =>
            {
                Assert.That(response.IsComplete, Is.True);
                Assert.That(response.IsCancelled, Is.EqualTo(requestStop));
                Assert.That(response.BookResults.Select(group => group.Status),
                    Is.EqualTo(hasPendingBook ? new[] { "complete", "complete", requestStop ? "pending" : "complete" } : new[] { "complete", "complete" }));
                Assert.That(response.BookResults.SelectMany(group => group.Releases).Select(release => release.Guid),
                    Is.EqualTo(requestStop || !hasPendingBook ? new[] { $"retained-{first.Id}", $"retained-{active.Id}" } : new[] { $"retained-{first.Id}", $"retained-{active.Id}", $"retained-{pending.Id}" }));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id),
                    Is.EqualTo(requestStop || !hasPendingBook ? new[] { first.Id, active.Id } : new[] { first.Id, active.Id, pending.Id }));
                Assert.That(broadcaster.LastResult.IsComplete, Is.True);
                Assert.That(broadcaster.LastResult.IsCancelled, Is.EqualTo(requestStop));
            });

            using var afterCompletion = await client.DeleteAsync($"/api/v1/release?searchId={searchId}");
            afterCompletion.EnsureSuccessStatusCode();
            Assert.That((await afterCompletion.Content.ReadFromJsonAsync<AuthorReleaseSearchStopResponse>()).IsStopping, Is.False);
        }

        private class SearchPrioritizeProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IPrioritizeDownloadDecision.PrioritizeDecisions))
                {
                    return args[0];
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private sealed class SearchBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => true;
            public AuthorReleaseSearchResponse LastResult { get; private set; }
            public int? FirstPublishedBookResultCount { get; private set; }

            public Task BroadcastMessage(SignalRMessage message)
            {
                LastResult = (AuthorReleaseSearchResponse)message.Body;
                FirstPublishedBookResultCount ??= LastResult.BookResults.Count;
                return Task.CompletedTask;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void selected_search_keeps_per_book_times_and_single_manual_unmonitored_search(bool unmonitoredSingle)
        {
            var author = BuildAuthor();
            var catalog = Enumerable.Range(101, 3).Select(id => BuildBook(id, BookMediaType.Audiobook, author, DateTime.MinValue)).ToList();
            if (unmonitoredSingle)
            {
                author.AudiobookMonitored = false;
                catalog.ForEach(book => book.AudiobookMonitored = false);
            }
            var harness = BuildHarness(catalog, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
            var processor = InterfaceProxy.Proxy<NzbDrone.Core.Download.IProcessDownloadDecisions>((_, _) =>
                Task.FromResult(new NzbDrone.Core.Download.ProcessedDecisions(new(), new(), new())));
            var service = new BookSearchService(harness.Subject, (IBookService)(object)harness.BookService, null, null, processor, LogManager.GetCurrentClassLogger());
            var selected = unmonitoredSingle ? new List<int> { 102 } : new List<int> { 103, 101, 102 };
            var before = DateTime.UtcNow;

            service.Execute(new BookSearchCommand(selected) { Trigger = NzbDrone.Core.Messaging.Commands.CommandTrigger.Manual });

            Assert.Multiple(() =>
            {
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.BookService.SearchTimeUpdates, Is.EqualTo(selected));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id), Is.EqualTo(selected));
                Assert.That(catalog.Where(book => selected.Contains(book.Id)).All(book => book.LastSearchTime >= before), Is.True);
                Assert.That(catalog.Where(book => !selected.Contains(book.Id)).All(book => book.LastSearchTime == DateTime.MinValue), Is.True);
            });
        }

        private static Author BuildAuthor()
        {
            return new Author
            {
                Id = 10,
                Name = "Callback Author",
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
        }

        private static Book BuildBook(int id, BookMediaType mediaType, Author author, DateTime lastSearchTime)
        {
            return new Book
            {
                Id = id,
                Title = $"Book {id}",
                Author = author,
                AuthorId = author.Id,
                MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                EbookMonitored = mediaType == BookMediaType.Ebook,
                LastSearchTime = lastSearchTime
            };
        }

        private static void SetMonitored(Book book, BookMediaType mediaType, bool monitored)
        {
            if (mediaType == BookMediaType.Audiobook)
            {
                book.AudiobookMonitored = monitored;
                book.EbookMonitored = false;
                return;
            }

            book.AudiobookMonitored = false;
            book.EbookMonitored = monitored;
        }

        private static Book CloneBookWithMonitoring(Book source, Author author, bool monitored)
        {
            var clone = BuildBook(source.Id, source.MediaType, author, source.LastSearchTime ?? DateTime.MinValue);
            SetMonitored(clone, source.MediaType, monitored);
            return clone;
        }

        private static ReleaseInfo BuildRelease(string guid)
        {
            return new ReleaseInfo { Guid = guid, Title = "Callback Author - Book", Indexer = "Offline" };
        }

        private static List<DownloadDecision> BuildDecisions(IEnumerable<ReleaseInfo> reports)
        {
            return reports.Select(report => new DownloadDecision(new RemoteBook { Release = report })).ToList();
        }

        private static TestHarness BuildHarness(List<Book> catalog, Author author)
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var indexerFactory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var indexer = DispatchProxy.Create<IIndexer, IndexerProxy>();
            ((BookServiceProxy)(object)bookService).Catalog = catalog;
            ((AuthorServiceProxy)(object)authorService).Author = author;
            ((IndexerFactoryProxy)(object)indexerFactory).Indexers.Add(indexer);
            var subject = new ReleaseSearchService(indexerFactory, bookService, authorService, null, decisionMaker,
                LogManager.GetLogger(nameof(ReleaseSearchServiceAuthorCallbacksFixture)));

            return new TestHarness
            {
                Subject = subject,
                BookService = (BookServiceProxy)(object)bookService,
                AuthorService = (AuthorServiceProxy)(object)authorService,
                DecisionMaker = (DecisionMakerProxy)(object)decisionMaker,
                Indexer = (IndexerProxy)(object)indexer
            };
        }

        private sealed class TestHarness
        {
            public ReleaseSearchService Subject { get; init; }
            public BookServiceProxy BookService { get; init; }
            public AuthorServiceProxy AuthorService { get; init; }
            public DecisionMakerProxy DecisionMaker { get; init; }
            public IndexerProxy Indexer { get; init; }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Catalog { get; set; }
            public int CatalogLoads { get; private set; }
            public List<int> SearchTimeUpdates { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    return Catalog;
                }

                if (targetMethod.Name == nameof(IBookService.GetBooks) || targetMethod.Name == nameof(IBookService.GetExistingBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Catalog.Where(book => ids.Contains(book.Id)).ToList();
                }

                if (targetMethod.Name == nameof(IBookService.UpdateLastSearchTime))
                {
                    SearchTimeUpdates.AddRange(((IEnumerable<Book>)args[0]).Select(book => book.Id));
                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }
            public int AuthorLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.GetAuthor))
                {
                    AuthorLoads++;
                    return Author;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public List<IIndexer> Indexers { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IIndexerFactory.AutomaticSearchEnabled) ||
                    targetMethod.Name == nameof(IIndexerFactory.InteractiveSearchEnabled))
                {
                    return Indexers;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerProxy : DispatchProxy
        {
            public Func<SearchCriteriaBase, Task<IList<ReleaseInfo>>> FetchHandler { get; set; }
            public List<SearchCriteriaBase> Searches { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_Definition")
                {
                    return new IndexerDefinition { Name = "Offline", Tags = new HashSet<int>() };
                }

                if (targetMethod.Name == nameof(IIndexer.Fetch))
                {
                    var criteria = (SearchCriteriaBase)args[0];
                    Searches.Add(criteria);
                    return FetchHandler(criteria);
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class DecisionMakerProxy : DispatchProxy
        {
            public Func<IEnumerable<ReleaseInfo>, SearchCriteriaBase, List<DownloadDecision>> DecisionHandler { get; set; } = (reports, _) => BuildDecisions(reports);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    return DecisionHandler((IEnumerable<ReleaseInfo>)args[0], (SearchCriteriaBase)args[1]);
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }
    }
}
