using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.DecisionEngine;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.REST;
using BadRequestException = Chaptarr.Http.REST.BadRequestException;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public partial class ReleaseControllerGroupedSearchFixture
    {
        private const int AuthorId = 71;
        private const int IndexerId = 9;
        private const string SearchId = "7f2f1c72-3491-451a-946f-4222fc0dcb73";
        private const string SharedGuid = "same-indexer-guid";

        [TestCase(BookMediaType.Ebook)]
        [TestCase(BookMediaType.Audiobook)]
        public async Task preferred_resource_and_stream_should_follow_torrent_health_before_size(BookMediaType mediaType)
        {
            var delay = DispatchProxy.Create<IDelayProfileService, DownloadDecisionSizeTieBreakerFixture.DelayProxy>();
            var prioritizer = new DownloadDecisionPriorizationService(null, delay, null, LogManager.GetCurrentClassLogger());
            var harness = CreateHarness(prioritizer: prioritizer);
            var author = CreateAuthor();
            var book = CreateBook(101, "Ranking test", author);
            book.MediaType = mediaType;
            var large = CreateDecision(author, book, "large");
            var small = CreateDecision(author, book, "healthy");
            large.RemoteBook.Release.Size = 400_000_000;
            small.RemoteBook.Release.Size = 4_000_000;
            foreach (var decision in new[] { large, small })
            {
                decision.RemoteBook.Release.DownloadProtocol = DownloadProtocol.Torrent;
                decision.RemoteBook.ParsedBookInfo.Quality = new QualityModel(mediaType == BookMediaType.Ebook ? Quality.PDF : Quality.MP3);
            }

            ((TorrentInfo)large.RemoteBook.Release).Seeders = 5;
            ((TorrentInfo)small.RemoteBook.Release).Seeders = 500;
            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(new[] { book });
                await onBookCompleted(book, new BookSearchResult { Decisions = new List<DownloadDecision> { large, small } }, null);
            };
            var response = await GetGroupedResults(harness, SearchId, mediaType);
            var streamed = GroupedMessages(harness.Broadcaster).SelectMany(message => message.BookResults)
                .Where(group => group.Releases.Count > 0);
            foreach (var group in response.BookResults.Concat(streamed))
            {
                Assert.That(group.Releases.Single(release => release.IsPreferredChoice).Guid, Is.EqualTo("healthy"));
                Assert.That(group.Releases.First().AutoPickReasons, Is.EqualTo(new[] { "Better torrent health" }));
            }

            harness.Search.CancellableFlatHandler = (_, _, _, _, _, _) => Task.FromResult(new List<DownloadDecision> { large, small });
            var legacy = (ReleaseSearchResponse)await harness.Controller.GetReleases(null, AuthorId, mediaType: mediaType.ToString().ToLowerInvariant());
            Assert.That(legacy.Releases.Single(release => release.IsPreferredChoice).Guid, Is.EqualTo("healthy"));
            Assert.That(legacy.Releases.First().AutoPickReasons, Is.EqualTo(new[] { "Better torrent health" }));
        }

        [TestCase(0, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(2, 2)]
        public async Task grouped_result_preserves_operation_local_indexer_outcomes(int queried, int failed)
        {
            var harness = CreateHarness();
            var book = CreateBook(101, "Mort", CreateAuthor());
            var outcome = new BookSearchResult();
            for (var id = 1; id <= queried; id++) outcome.Indexers.RecordRequest(id);
            for (var id = 1; id <= failed; id++) outcome.Indexers.RecordFailure(id);
            harness.Search.Handler = async (_, _, _, _, _, started, completed, _) =>
            {
                await started(new[] { book });
                await completed(book, outcome, null);
            };
            var response = await GetGroupedResults(harness);
            var group = response.BookResults.Single();
            Assert.That(group.IndexersQueried, Is.EqualTo(queried));
            Assert.That(group.IndexersFailed, Is.EqualTo(failed));
            Assert.That(group.Status, Is.EqualTo("complete"));
            Assert.That(group.Releases, Is.Empty);
        }

        [Test]
        public async Task empty_author_catalog_should_publish_an_empty_roster_and_complete_response()
        {
            var harness = CreateHarness();
            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, _, _) =>
            {
                await onSearchStarted(Array.Empty<Book>());
            };

            var response = await GetGroupedResults(harness);
            var messages = GroupedMessages(harness.Broadcaster);

            Assert.Multiple(() =>
            {
                Assert.That(response.SearchId, Is.EqualTo(SearchId));
                Assert.That(response.AuthorId, Is.EqualTo(AuthorId));
                Assert.That(response.MediaType, Is.EqualTo("audiobook"));
                Assert.That(response.BookResults, Is.Empty);
                Assert.That(response.IsComplete, Is.True);
                Assert.That(response.IsCancelled, Is.False);
                Assert.That(messages, Has.Count.EqualTo(2));
                Assert.That(messages[0].IsComplete, Is.False);
                Assert.That(messages[0].BookResults, Is.Empty);
                Assert.That(messages[1].IsComplete, Is.True);
                Assert.That(messages[1].BookResults, Is.Empty);
            });

            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
        }

        [Test]
        public async Task same_indexer_guid_should_remain_scoped_per_book_when_completions_arrive_out_of_order()
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var firstBook = CreateBook(101, "The First Story", author);
            var secondBook = CreateBook(202, "The Second Story", author);
            var visibleRejectedDecision = CreateDecision(author, firstBook, "visible-rejected-guid",
                new Rejection("Rejected by preference", RejectionType.Permanent, true, "Quality"));
            var firstDecision = CreateDecision(author, firstBook, SharedGuid);
            var hiddenIndexZeroDecision = CreateDecision(author, secondBook, "hidden-pack-guid",
                new Rejection("Release contains multiple books", RejectionType.Permanent, true, "Pack"));
            var secondDecision = CreateDecision(author, secondBook, SharedGuid);

            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(new[] { firstBook, secondBook });
                await onBookCompleted(secondBook, new BookSearchResult { Decisions = new List<DownloadDecision> { hiddenIndexZeroDecision, secondDecision } }, null);
                await onBookCompleted(firstBook, new BookSearchResult { Decisions = new List<DownloadDecision> { visibleRejectedDecision, firstDecision } }, null);
            };

            var response = await GetGroupedResults(harness);
            var firstGroup = response.BookResults.Single(group => group.BookId == firstBook.Id);
            var secondGroup = response.BookResults.Single(group => group.BookId == secondBook.Id);
            var visibleRejectedRelease = firstGroup.Releases.Single(release => release.Guid == "visible-rejected-guid");
            var firstRelease = firstGroup.Releases.Single(release => release.Guid == SharedGuid);
            var secondRelease = secondGroup.Releases.Single(release => release.Guid == SharedGuid);
            var hiddenIndexZeroRelease = secondGroup.HiddenReleases.Single();
            var progress = GroupedMessages(harness.Broadcaster);
            var streamedReleases = progress.Where(message => !message.IsComplete)
                .SelectMany(message => message.BookResults)
                .SelectMany(group => group.Releases.Concat(group.HiddenReleases))
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(response.IsComplete, Is.True);
                Assert.That(firstGroup.Status, Is.EqualTo("complete"));
                Assert.That(secondGroup.Status, Is.EqualTo("complete"));
                Assert.That(firstRelease.Guid, Is.EqualTo(SharedGuid));
                Assert.That(secondRelease.Guid, Is.EqualTo(SharedGuid));
                Assert.That(firstRelease.IndexerId, Is.EqualTo(IndexerId));
                Assert.That(secondRelease.IndexerId, Is.EqualTo(IndexerId));
                Assert.That(firstRelease.SearchId, Is.EqualTo(SearchId));
                Assert.That(secondRelease.SearchId, Is.EqualTo(SearchId));
                Assert.That(firstRelease.SearchedBookId, Is.EqualTo(firstBook.Id));
                Assert.That(secondRelease.SearchedBookId, Is.EqualTo(secondBook.Id));
                Assert.That(firstRelease.BookId, Is.EqualTo(firstBook.Id));
                Assert.That(secondRelease.BookId, Is.EqualTo(secondBook.Id));
                Assert.That(visibleRejectedRelease.Approved, Is.False);
                Assert.That(visibleRejectedRelease.IsPreferredChoice, Is.False);
                Assert.That(firstRelease.Rank, Is.EqualTo(2));
                Assert.That(secondRelease.Rank, Is.EqualTo(1));
                Assert.That(firstRelease.IsPreferredChoice, Is.True);
                Assert.That(secondRelease.IsPreferredChoice, Is.True);
                Assert.That(hiddenIndexZeroRelease.Rank, Is.EqualTo(2));
                Assert.That(hiddenIndexZeroRelease.IsPreferredChoice, Is.False);
                Assert.That(firstRelease.DownloadUrl, Is.Null);
                Assert.That(firstRelease.MagnetUrl, Is.Null);
                Assert.That(secondRelease.DownloadUrl, Is.Null);
                Assert.That(secondRelease.MagnetUrl, Is.Null);
                Assert.That(streamedReleases, Has.Count.EqualTo(4));
                Assert.That(streamedReleases.All(release => release.DownloadUrl == null && release.MagnetUrl == null), Is.True);
                Assert.That(progress[0].BookResults.Select(book => book.Status), Is.All.EqualTo("pending"));
                Assert.That(progress.Skip(1).Where(message => !message.IsComplete).SelectMany(message => message.BookResults).Select(book => book.BookId),
                    Is.EqualTo(new[] { secondBook.Id, firstBook.Id }));
                Assert.That(progress.Last().IsComplete, Is.True);
            });

            await Grab(harness.Controller, firstRelease);
            await Grab(harness.Controller, secondRelease);

            Assert.That(harness.Downloads.Reports.Select(report => report.RemoteBook.Books.Single().Id),
                Is.EqualTo(new[] { firstBook.Id, secondBook.Id }));

            harness.Search.CancellableFlatHandler = (_, _, _, _, _, _) => Task.FromResult(new List<DownloadDecision> { firstDecision });
            var legacy = (ReleaseSearchResponse)await harness.Controller.GetReleases(null, AuthorId, mediaType: "audiobook");

            Assert.Multiple(() =>
            {
                Assert.That(legacy.Releases, Has.Count.EqualTo(1));
                Assert.That(legacy.Releases[0].DownloadUrl, Is.EqualTo("https://indexer.example/download/shared"));
                Assert.That(legacy.Releases[0].MagnetUrl, Is.EqualTo("magnet:?xt=urn:btih:shared"));
            });
        }

        [Test]
        public async Task a_completed_book_can_be_grabbed_while_the_next_book_is_still_searching()
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var firstBook = CreateBook(301, "Available Now", author);
            var secondBook = CreateBook(302, "Still Searching", author);
            var firstDecision = CreateDecision(author, firstBook, "early-grab-guid");
            var secondDecision = CreateDecision(author, secondBook, "later-guid");
            var firstBookPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSecondBook = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception streamedGrabFailure = null;
            harness.Broadcaster.OnBroadcast = async message =>
            {
                if (message.Body is AuthorReleaseSearchResponse envelope)
                {
                    var completedBook = envelope.BookResults.SingleOrDefault(group => group.BookId == firstBook.Id && group.Status == "complete");
                    if (completedBook != null)
                    {
                        try
                        {
                            await Grab(harness.Controller, completedBook.Releases.Single());
                        }
                        catch (Exception ex)
                        {
                            streamedGrabFailure = ex;
                        }
                    }
                }
            };

            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(new[] { firstBook, secondBook });
                await onBookCompleted(firstBook, new BookSearchResult { Decisions = new List<DownloadDecision> { firstDecision } }, null);
                firstBookPublished.TrySetResult(true);
                await allowSecondBook.Task;
                await onBookCompleted(secondBook, new BookSearchResult { Decisions = new List<DownloadDecision> { secondDecision } }, null);
            };

            var searchTask = GetGroupedResults(harness);
            await firstBookPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstBookUpdate = GroupedMessages(harness.Broadcaster)
                .SelectMany(message => message.BookResults)
                .Single(group => group.BookId == firstBook.Id && group.Status == "complete");
            Assert.Multiple(() =>
            {
                Assert.That(firstBookUpdate.Releases, Has.Count.EqualTo(1));
                Assert.That(streamedGrabFailure, Is.Null);
                Assert.That(harness.Downloads.Reports.Single().RemoteBook.Books.Single().Id, Is.EqualTo(firstBook.Id));
            });

            allowSecondBook.TrySetResult(true);
            var finalResponse = await searchTask;
            Assert.That(finalResponse.IsComplete, Is.True);
            Assert.That(finalResponse.BookResults.All(group => group.Status == "complete"), Is.True);
        }

        [Test]
        public void invalid_grouped_search_scope_or_id_should_fail_before_calling_the_search_service()
        {
            var harness = CreateHarness();
            var invalidRequests = new (string mediaType, string searchId)[]
            {
                (null, SearchId),
                ("invalid", SearchId),
                ("audiobook", "not-a-uuid")
            };

            foreach (var (mediaType, searchId) in invalidRequests)
            {
                Assert.ThrowsAsync<BadRequestException>(() =>
                    harness.Controller.GetReleases(null, AuthorId, mediaType: mediaType, searchId: searchId));
            }

            Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
        }


        [Test]
        public async Task explicit_stop_from_another_controller_should_finish_the_current_book_and_retain_its_result()
        {
            var sharedCache = new CacheManager();
            var searchHarness = CreateHarness(cacheManager: sharedCache);
            var stopHarness = CreateHarness(cacheManager: sharedCache);
            var author = CreateAuthor();
            var completedBook = CreateBook(501, "Finishes Before Stop", author);
            var pendingBook = CreateBook(502, "Remains Pending", author);
            var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCurrentBookToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchToken = CancellationToken.None;

            searchHarness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, token) =>
            {
                searchToken = token;
                await onSearchStarted(new[] { completedBook, pendingBook });
                searchStarted.TrySetResult(true);
                await allowCurrentBookToFinish.Task;
                await onBookCompleted(completedBook,
                    new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, completedBook, "finished-after-stop") } }, null);
                token.ThrowIfCancellationRequested();
            };

            var searchTask = GetGroupedResults(searchHarness);
            await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stopResponse = stopHarness.Controller.StopAuthorSearch(SearchId);
            Assert.Multiple(() =>
            {
                Assert.That(searchHarness.Controller.HttpContext.RequestAborted.IsCancellationRequested, Is.False);
                Assert.That(stopResponse.IsStopping, Is.True);
                Assert.That(searchToken.IsCancellationRequested, Is.True);
            });

            allowCurrentBookToFinish.TrySetResult(true);
            var response = await searchTask;
            var completedGroup = response.BookResults.Single(group => group.BookId == completedBook.Id);
            var pendingGroup = response.BookResults.Single(group => group.BookId == pendingBook.Id);

            Assert.Multiple(() =>
            {
                Assert.That(response.IsComplete, Is.True);
                Assert.That(response.IsCancelled, Is.True);
                Assert.That(completedGroup.Status, Is.EqualTo("complete"));
                Assert.That(completedGroup.Releases.Select(release => release.Guid), Is.EqualTo(new[] { "finished-after-stop" }));
                Assert.That(pendingGroup.Status, Is.EqualTo("pending"));
                Assert.That(stopHarness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            });
        }

        [Test]
        public async Task repeated_stop_should_remain_true_while_active_and_uuid_formatting_should_be_canonicalized()
        {
            var sharedCache = new CacheManager();
            var searchHarness = CreateHarness(cacheManager: sharedCache);
            var stopHarness = CreateHarness(cacheManager: sharedCache);
            var author = CreateAuthor();
            var completedBook = CreateBook(511, "Current Book", author);
            var pendingBook = CreateBook(512, "Next Book", author);
            var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSearchToObserveCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchToken = CancellationToken.None;
            searchHarness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, token) =>
            {
                searchToken = token;
                await onSearchStarted(new[] { completedBook, pendingBook });
                searchStarted.TrySetResult(true);
                await allowSearchToObserveCancellation.Task;
                await onBookCompleted(completedBook,
                    new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, completedBook, "current-book-complete") } }, null);
                token.ThrowIfCancellationRequested();
            };

            var searchTask = GetGroupedResults(searchHarness);
            await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var searchGuid = Guid.Parse(SearchId);
            var firstStop = stopHarness.Controller.StopAuthorSearch(searchGuid.ToString("B"));
            var secondStop = stopHarness.Controller.StopAuthorSearch(searchGuid.ToString("P"));
            Assert.Multiple(() =>
            {
                Assert.That(firstStop.IsStopping, Is.True);
                Assert.That(secondStop.IsStopping, Is.True);
                Assert.That(searchToken.IsCancellationRequested, Is.True);
            });

            allowSearchToObserveCancellation.TrySetResult(true);
            var response = await searchTask;
            Assert.Multiple(() =>
            {
                Assert.That(response.IsCancelled, Is.True);
                Assert.That(response.BookResults.Single(group => group.BookId == completedBook.Id).Status, Is.EqualTo("complete"));
                Assert.That(response.BookResults.Single(group => group.BookId == pendingBook.Id).Status, Is.EqualTo("pending"));
                Assert.That(stopHarness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            });
        }

        [Test]
        public async Task stopping_one_of_two_shared_cache_searches_should_not_cancel_the_other()
        {
            const string secondSearchId = "1cf23748-41cb-4738-aa54-42b62ed8f39d";
            var sharedCache = new CacheManager();
            var firstHarness = CreateHarness(cacheManager: sharedCache);
            var secondHarness = CreateHarness(cacheManager: sharedCache);
            var stopHarness = CreateHarness(cacheManager: sharedCache);
            var author = CreateAuthor();
            var firstCompletedBook = CreateBook(521, "First Search Current Book", author);
            var firstPendingBook = CreateBook(522, "First Search Next Book", author);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowFirstToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSecondToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstToken = CancellationToken.None;
            var secondToken = CancellationToken.None;

            firstHarness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, token) =>
            {
                firstToken = token;
                await onSearchStarted(new[] { firstCompletedBook, firstPendingBook });
                firstStarted.TrySetResult(true);
                await allowFirstToFinish.Task;
                await onBookCompleted(firstCompletedBook,
                    new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, firstCompletedBook, "isolated-current-book") } }, null);
                token.ThrowIfCancellationRequested();
            };
            secondHarness.Search.Handler = async (_, _, _, _, _, onSearchStarted, _, token) =>
            {
                secondToken = token;
                await onSearchStarted(Array.Empty<Book>());
                secondStarted.TrySetResult(true);
                await allowSecondToFinish.Task;
            };

            var firstTask = GetGroupedResults(firstHarness);
            var secondTask = GetGroupedResults(secondHarness, secondSearchId, BookMediaType.Ebook);
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

            var stopResponse = stopHarness.Controller.StopAuthorSearch(SearchId);
            Assert.Multiple(() =>
            {
                Assert.That(stopResponse.IsStopping, Is.True);
                Assert.That(firstToken.IsCancellationRequested, Is.True);
                Assert.That(secondToken.IsCancellationRequested, Is.False);
            });

            allowFirstToFinish.TrySetResult(true);
            allowSecondToFinish.TrySetResult(true);
            var responses = await Task.WhenAll(firstTask, secondTask);

            Assert.Multiple(() =>
            {
                Assert.That(responses[0].IsCancelled, Is.True);
                Assert.That(responses[1].IsCancelled, Is.False);
                Assert.That(stopHarness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
                Assert.That(stopHarness.Controller.StopAuthorSearch(secondSearchId).IsStopping, Is.False);
            });
        }

        [Test]
        public async Task invalid_or_unknown_stop_id_should_not_cancel_an_active_search()
        {
            var harness = CreateHarness();
            var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSearchToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchToken = CancellationToken.None;
            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, _, token) =>
            {
                searchToken = token;
                await onSearchStarted(Array.Empty<Book>());
                searchStarted.TrySetResult(true);
                await allowSearchToFinish.Task;
            };

            var searchTask = GetGroupedResults(harness);
            await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.Throws<BadRequestException>(() => harness.Controller.StopAuthorSearch(null));
                Assert.Throws<BadRequestException>(() => harness.Controller.StopAuthorSearch("not-a-uuid"));
                Assert.That(harness.Controller.StopAuthorSearch("7f2f1c72-3491-451a-946f-4222fc0dcb74").IsStopping, Is.False);
                Assert.That(searchToken.IsCancellationRequested, Is.False);
            });

            allowSearchToFinish.TrySetResult(true);
            var response = await searchTask;
            Assert.That(response.IsCancelled, Is.False);
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
        }

        [Test]
        public async Task duplicate_active_author_and_format_should_attach_without_disturbing_the_first_search()
        {
            var sharedCache = new CacheManager();
            var firstHarness = CreateHarness(cacheManager: sharedCache);
            var duplicateHarness = CreateHarness(cacheManager: sharedCache);
            var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSearchToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchToken = CancellationToken.None;
            firstHarness.Search.Handler = async (_, _, _, _, _, onSearchStarted, _, token) =>
            {
                searchToken = token;
                await onSearchStarted(Array.Empty<Book>());
                searchStarted.TrySetResult(true);
                await allowSearchToFinish.Task;
            };

            var firstTask = GetGroupedResults(firstHarness);
            await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var duplicate = (AuthorReleaseSearchResponse)await duplicateHarness.Controller.GetReleases(null, AuthorId,
                mediaType: "audiobook", searchId: Guid.NewGuid().ToString());
            Assert.Multiple(() =>
            {
                Assert.That(duplicate.SearchId, Is.EqualTo(SearchId));
                Assert.That(duplicateHarness.Driver.Commands, Is.Empty);
                Assert.That(searchToken.IsCancellationRequested, Is.False);
            });

            allowSearchToFinish.TrySetResult(true);
            var response = await firstTask;
            Assert.Multiple(() =>
            {
                Assert.That(response.IsComplete, Is.True);
                Assert.That(response.IsCancelled, Is.False);
                Assert.That(firstHarness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            });
        }

        [Test]
        public async Task scoped_grab_should_reject_a_posted_book_id_that_differs_from_the_search_target()
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var book = CreateBook(401, "Scoped Only", author);
            var decision = CreateDecision(author, book, "scoped-mismatch-guid");
            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(new[] { book });
                await onBookCompleted(book, new BookSearchResult { Decisions = new List<DownloadDecision> { decision } }, null);
            };

            var response = await GetGroupedResults(harness);
            var release = response.BookResults.Single().Releases.Single();
            release.BookId = book.Id + 1;

            var exception = Assert.ThrowsAsync<NzbDroneClientException>(() => Grab(harness.Controller, release));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Conflict));
                Assert.That(harness.Downloads.Reports, Is.Empty);
            });
        }

        [Test]
        public async Task per_book_error_callback_should_publish_a_failed_group_and_complete_the_batch()
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var firstBook = CreateBook(601, "Completed Before Failure", author);
            var failingBook = CreateBook(602, "Failed Book", author);
            var lastBook = CreateBook(603, "Completed After Failure", author);
            var books = new[] { firstBook, failingBook, lastBook };
            var decisions = new[]
            {
                CreateDecision(author, firstBook, "before-failure-guid"),
                CreateDecision(author, lastBook, "after-failure-guid")
            };
            var failure = new InvalidOperationException("Per-book result mapping failed");

            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(books);
                await onBookCompleted(firstBook, new BookSearchResult { Decisions = new List<DownloadDecision> { decisions[0] } }, null);
                await onBookCompleted(failingBook, new BookSearchResult { Decisions = new List<DownloadDecision>() }, failure);
                await onBookCompleted(lastBook, new BookSearchResult { Decisions = new List<DownloadDecision> { decisions[1] } }, null);
            };

            var response = await GetGroupedResults(harness);
            var firstGroup = response.BookResults.Single(group => group.BookId == firstBook.Id);
            var failedGroup = response.BookResults.Single(group => group.BookId == failingBook.Id);
            var lastGroup = response.BookResults.Single(group => group.BookId == lastBook.Id);
            var finalMessage = GroupedMessages(harness.Broadcaster).Last();

            Assert.Multiple(() =>
            {
                Assert.That(firstGroup.Status, Is.EqualTo("complete"));
                Assert.That(firstGroup.Releases.Select(release => release.Guid), Is.EqualTo(new[] { "before-failure-guid" }));
                Assert.That(failedGroup.Status, Is.EqualTo("failed"));
                Assert.That(failedGroup.Releases, Is.Empty);
                Assert.That(lastGroup.Status, Is.EqualTo("complete"));
                Assert.That(lastGroup.Releases.Select(release => release.Guid), Is.EqualTo(new[] { "after-failure-guid" }));
                Assert.That(response.IsComplete, Is.True);
                Assert.That(finalMessage.IsComplete, Is.True);
                Assert.That(finalMessage.IsCancelled, Is.False);
            });
        }

        [Test]
        public void unrecoverable_grouped_callback_failure_should_retain_failed_pending_groups()
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var completedBook = CreateBook(611, "Already Completed", author);
            var pendingBook = CreateBook(612, "Callback Failed", author);
            var failure = new InvalidOperationException("Error result callback failed");
            harness.Search.Handler = async (_, _, _, _, _, onSearchStarted, onBookCompleted, _) =>
            {
                await onSearchStarted(new[] { completedBook, pendingBook });
                await onBookCompleted(completedBook, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, completedBook, "completed-before-error") } }, null);
                throw failure;
            };

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => GetGroupedResults(harness));
            var finalMessage = GroupedMessages(harness.Broadcaster).Last();
            var failedGroup = harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults.Single(group => group.Status == "failed");

            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.SameAs(failure));
                Assert.That(finalMessage.IsComplete, Is.True);
                Assert.That(finalMessage.IsCancelled, Is.False);
                Assert.That(failedGroup.BookId, Is.EqualTo(pendingBook.Id));
                Assert.That(failedGroup.Status, Is.EqualTo("failed"));
                Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            });
        }

        [Test]
        public async Task disconnecting_the_admission_request_must_not_cancel_background_work()
        {
            using var cancellation = new CancellationTokenSource();
            var harness = CreateHarness(cancellation.Token);
            var author = CreateAuthor();
            var first = CreateBook(621, "First", author);
            var second = CreateBook(622, "Second", author);
            harness.Search.Handler = async (_, _, _, _, _, started, completed, token) =>
            {
                await started(new[] { first, second });
                await completed(first, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, first, "first") } }, null);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                await completed(second, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, second, "second") } }, null);
            };

            var response = await GetGroupedResults(harness);
            Assert.That(response.IsCancelled, Is.False);
            Assert.That(response.BookResults.Select(group => group.Status), Is.EqualTo(new[] { "complete", "complete" }));
        }

        [Test]
        public async Task admission_returns_before_search_and_stop_works_while_queued()
        {
            var harness = CreateHarness();
            var acknowledgement = (AuthorReleaseSearchResponse)await harness.Controller.GetReleases(null, AuthorId,
                mediaType: "audiobook", searchId: SearchId);
            Assert.That(acknowledgement.IsComplete, Is.False);
            Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            await harness.Driver.Run(SearchId);
            Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
            Assert.That(GroupedMessages(harness.Broadcaster).Last().IsCancelled, Is.True);
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
        }

        [Test]
        public async Task failed_admission_should_release_the_uuid_for_a_new_attempt()
        {
            var harness = CreateHarness();
            harness.Driver.EnqueueFailure = new InvalidOperationException("Queue unavailable");
            Assert.ThrowsAsync<InvalidOperationException>(() => harness.Controller.GetReleases(null, AuthorId,
                mediaType: "audiobook", searchId: SearchId));
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            harness.Driver.EnqueueFailure = null;
            await harness.Controller.GetReleases(null, AuthorId, mediaType: "audiobook", searchId: SearchId);
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).IsCancelled, Is.True);
            await harness.Driver.Run(SearchId);
        }

        [Test]
        public async Task a_restored_command_without_its_interactive_session_must_not_repeat_indexer_queries()
        {
            var beforeRestart = CreateHarness();
            await beforeRestart.Controller.GetReleases(null, AuthorId, mediaType: "audiobook", searchId: SearchId);
            var restored = beforeRestart.Driver.Commands[SearchId];
            var afterRestart = CreateHarness();
            var error = Assert.ThrowsAsync<CommandFailedException>(() => afterRestart.Driver.Service.Run(restored, CancellationToken.None));
            Assert.That(error.Message, Does.Contain("Search interrupted").And.Contain("Start a new search"));
            Assert.That(afterRestart.Search.AuthorSearchCalls, Is.Zero);
            Assert.That(afterRestart.Broadcaster.Messages, Is.Empty);
            beforeRestart.Controller.StopAuthorSearch(SearchId);
            await beforeRestart.Driver.Run(SearchId);
        }

        [Test]
        public void failure_before_the_roster_must_publish_a_terminal_error()
        {
            var harness = CreateHarness();
            harness.Search.Handler = (_, _, _, _, _, _, _, _) => Task.FromException(new InvalidOperationException("Catalog unavailable"));
            Assert.ThrowsAsync<InvalidOperationException>(() => GetGroupedResults(harness));
            var final = GroupedMessages(harness.Broadcaster).Last();
            Assert.That(final.IsComplete, Is.True);
            Assert.That(final.IsFailed, Is.True);
            Assert.That(final.BookResults, Is.Empty);
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
        }

        [Test]
        public void legacy_author_disconnect_should_not_be_wrapped_as_a_server_error()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var harness = CreateHarness(cancellation.Token);
            var disconnected = new OperationCanceledException(cancellation.Token);
            harness.Search.CancellableFlatHandler = (_, _, _, _, _, token) =>
            {
                Assert.That(token, Is.EqualTo(cancellation.Token));
                return Task.FromException<List<DownloadDecision>>(disconnected);
            };

            var thrown = Assert.ThrowsAsync<OperationCanceledException>(() =>
                harness.Controller.GetReleases(null, AuthorId, mediaType: "audiobook"));

            Assert.That(thrown, Is.SameAs(disconnected));
        }

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        [TestCase(false, 4)]
        [TestCase(true, 4)]
        [TestCase(false, 5)]
        [TestCase(true, 5)]
        public void native_command_worker_should_execute_or_cancel_the_grouped_search(bool postgres, int stopMode)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = CreateHarness();
                var author = CreateAuthor();
                var book = CreateBook(801, "Worker search", author);
                Action stopWhileRunning = null;
                harness.Search.Handler = async (_, _, _, _, _, started, completed, token) =>
                {
                    await started(new[] { book });
                    await completed(book, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(author, book, "worker-result") } }, null);
                    stopWhileRunning?.Invoke();
                    token.ThrowIfCancellationRequested();
                    if (stopMode == 4)
                    {
                        throw new InvalidOperationException("Controlled search failure");
                    }
                };
                var events = DispatchProxy.Create<IEventAggregator, CommandEventProxy>();
                var observed = (CommandEventProxy)(object)events;
                var logger = LogManager.GetCurrentClassLogger();
                var repository = new CommandRepository(database, events);
                var queue = new CommandQueueManager(repository, null, new KnownTypes(new List<Type> { typeof(InteractiveAuthorSearchCommand) }), logger);
                var cache = new CacheManager();
                var service = new GroupedAuthorSearchService((ISearchForReleases)(object)harness.Search,
                    DispatchProxy.Create<IPrioritizeDownloadDecision, PrioritizeProxy>(), queue,
                    new InteractiveReleaseMapper(cache, logger), harness.Broadcaster, cache, logger);
                using var container = new ServiceCollection().AddSingleton<IExecute<InteractiveAuthorSearchCommand>>(service).BuildServiceProvider();
                var executor = new CommandExecutor(new ServiceFactory(container), queue, events, logger);
                observed.Updated = service.HandleAsync;
                observed.Executed = service.HandleAsync;
                observed.OnCommandUpdated = update =>
                {
                    if (stopMode == 5 && update.Command.Status == CommandStatus.Started)
                    {
                        service.Stop(Guid.Parse(SearchId));
                    }
                };
                var admission = service.Start(AuthorId, BookMediaType.Audiobook, SearchId);
                var persisted = repository.All().Single();
                Assert.That(admission.CommandId, Is.EqualTo(persisted.Id));
                Assert.That(persisted.Body.SendUpdatesToClient, Is.True);
                Assert.That(persisted.Body.SuppressMessages, Is.False);
                Assert.That(persisted.Body, Is.TypeOf<InteractiveAuthorSearchCommand>());
                Assert.That(((InteractiveAuthorSearchCommand)persisted.Body).SearchId, Is.EqualTo(SearchId));
                Assert.That(((InteractiveAuthorSearchCommand)persisted.Body).MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(persisted.Priority, Is.EqualTo(CommandPriority.Normal));
                Assert.That(persisted.Trigger, Is.EqualTo(CommandTrigger.Manual));
                if (stopMode == 1)
                {
                    Assert.That(service.Stop(Guid.Parse(SearchId)).IsStopping, Is.False);
                    Assert.That(repository.Get(persisted.Id).Status, Is.EqualTo(CommandStatus.Cancelled));
                    Assert.That(GroupedMessages(harness.Broadcaster).Last().IsCancelled, Is.True);
                    Assert.That(service.Stop(Guid.Parse(SearchId)).IsStopping, Is.False);
                    Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
                    Assert.That(service.GetSnapshot(AuthorId, BookMediaType.Audiobook).IsCancelled, Is.True);
                    return;
                }

                if (stopMode == 2)
                {
                    stopWhileRunning = () => service.Stop(Guid.Parse(SearchId));
                }
                else if (stopMode == 3)
                {
                    stopWhileRunning = () => queue.Cancel(persisted.Id);
                }

                executor.Handle(new ApplicationStartedEvent());
                try
                {
                    observed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    Assert.That(repository.Get(persisted.Id).Status, Is.EqualTo(stopMode == 4 ? CommandStatus.Failed : (stopMode == 2 || stopMode == 3 || stopMode == 5) ? CommandStatus.Cancelled : CommandStatus.Completed));
                    Assert.That(GroupedMessages(harness.Broadcaster).Last().IsCancelled, Is.EqualTo(stopMode == 2 || stopMode == 3 || stopMode == 5));
                    if (stopMode == 5)
                    {
                        Assert.That(GroupedMessages(harness.Broadcaster).Last().BookResults, Is.Empty);
                    }
                    else
                    {
                        Assert.That(service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults.Single().Releases.Single().Guid, Is.EqualTo("worker-result"));
                    }
                    Assert.That(GroupedMessages(harness.Broadcaster).Last().IsFailed, Is.EqualTo(stopMode == 4));
                    Assert.That(observed.Statuses, Does.Contain(CommandStatus.Started));
                    Assert.That(observed.Statuses.Last(), Is.EqualTo(repository.Get(persisted.Id).Status));
                    var commandController = new Chaptarr.Api.V1.Commands.CommandController(queue, harness.Broadcaster, null, logger);
                    Assert.That(commandController.GetResourceByIdWithErrorHandler(persisted.Id).Value.Id, Is.EqualTo(admission.CommandId));
                    queue.Get(persisted.Id).EndedAt = DateTime.UtcNow.AddMinutes(-6);
                    queue.CleanCommands();
                    Assert.That(commandController.GetStartedCommands(), Is.Empty);
                    Assert.That(commandController.GetResourceByIdWithErrorHandler(persisted.Id).Value.Status,
                        Is.EqualTo(repository.Get(persisted.Id).Status));
                    Assert.That(harness.Search.AuthorSearchCalls, Is.EqualTo(stopMode == 5 ? 0 : 1));
                    Assert.That(service.Stop(Guid.Parse(SearchId)).IsStopping, Is.False);
                }
                finally
                {
                    executor.Handle(new ApplicationShutdownRequested());
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void restart_should_expose_persisted_search_state_without_reissuing_indexer_queries(bool postgres, bool wasStarted)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = DispatchProxy.Create<IEventAggregator, CommandEventProxy>();
                var logger = LogManager.GetCurrentClassLogger();
                var repository = new CommandRepository(database, events);
                var types = new KnownTypes(new List<Type> { typeof(InteractiveAuthorSearchCommand) });
                var queue = new CommandQueueManager(repository, null, types, logger);
                var queued = queue.Push(new InteractiveAuthorSearchCommand
                {
                    SearchId = SearchId, AuthorId = AuthorId, MediaType = BookMediaType.Audiobook, SendUpdatesToClient = true
                });
                if (wasStarted)
                {
                    Assert.That(queue.Queue(CancellationToken.None).First(), Is.SameAs(queued));
                    queue.Start(queued);
                }

                var restoredQueue = new CommandQueueManager(repository, null, types, logger);
                restoredQueue.Handle(new ApplicationStartedEvent());
                var harness = CreateHarness();
                var controller = new Chaptarr.Api.V1.Commands.CommandController(restoredQueue, harness.Broadcaster, types, logger);
                var recovered = controller.GetResourceByIdWithErrorHandler(queued.Id).Value;
                Assert.That(recovered.Status, Is.EqualTo(wasStarted ? CommandStatus.Orphaned : CommandStatus.Queued));
                Assert.That(((InteractiveAuthorSearchCommand)recovered.Body).SearchId, Is.EqualTo(SearchId));
                using var json = System.Text.Json.JsonDocument.Parse(NzbDrone.Common.Serializer.STJson.ToJson(recovered));
                Assert.That(json.RootElement.GetProperty("body").GetProperty("searchId").GetString(), Is.EqualTo(SearchId));
                Assert.That(json.RootElement.GetProperty("body").GetProperty("mediaType").GetString(), Is.EqualTo("audiobook"));
                Assert.That(json.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(queued.Id));
                var cache = new CacheManager();
                var service = new GroupedAuthorSearchService((ISearchForReleases)(object)harness.Search,
                    DispatchProxy.Create<IPrioritizeDownloadDecision, PrioritizeProxy>(), restoredQueue,
                    new InteractiveReleaseMapper(cache, logger), harness.Broadcaster, cache, logger);
                var error = Assert.Throws<CommandFailedException>(() => service.Execute((InteractiveAuthorSearchCommand)recovered.Body));
                Assert.That(error.Message, Does.Contain("Search interrupted"));
                Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
                Assert.That(GroupedMessages(harness.Broadcaster), Is.Empty);
            });
        }

        private class CommandEventProxy : DispatchProxy
        {
            public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Action<ModelEvent<CommandModel>> Updated { get; set; }
            public ConcurrentQueue<CommandStatus> Statuses { get; } = new();
            public Action<CommandUpdatedEvent> OnCommandUpdated { get; set; }
            public Action<CommandExecutedEvent> Executed { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (args[0] is ModelEvent<CommandModel> update)
                {
                    Updated?.Invoke(update);
                }

                if (args[0] is CommandUpdatedEvent commandUpdated)
                {
                    Statuses.Enqueue(commandUpdated.Command.Status);
                    OnCommandUpdated?.Invoke(commandUpdated);
                }

                if (args[0] is CommandExecutedEvent executed)
                {
                    Executed?.Invoke(executed);
                    Completed.TrySetResult(true);
                }

                return null;
            }
        }

        private static async Task<AuthorReleaseSearchResponse> GetGroupedResults(Harness harness, string searchId = SearchId, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            var acknowledgement = (AuthorReleaseSearchResponse)await harness.Controller.GetReleases(null, AuthorId,
                mediaType: mediaType.ToString().ToLowerInvariant(), searchId: searchId);
            Assert.That(acknowledgement.IsComplete, Is.False);
            await harness.Driver.Run(searchId);
            Assert.That(GroupedMessages(harness.Broadcaster).Last().BookResults, Is.Empty, "Completion is a notice; GET supplies the saved rows");
            return harness.Driver.Service.GetSnapshot(AuthorId, mediaType);
        }

        private static async Task Grab(ReleaseController controller, ReleaseResource release)
        {
            controller.HttpContext.Request.Method = HttpMethods.Post;
            await controller.DownloadRelease(release);
        }

        private static List<AuthorReleaseSearchResponse> GroupedMessages(RecordingBroadcaster broadcaster)
        {
            return broadcaster.Messages
                .Where(message => message.Name == "AuthorSearchResults")
                .Select(message => (AuthorReleaseSearchResponse)message.Body)
                .ToList();
        }

        private static Author CreateAuthor()
        {
            return new Author { Id = AuthorId, Name = "Grouped Search Author" };
        }

        private static Book CreateBook(int id, string title, Author author)
        {
            return new Book
            {
                Id = id,
                AuthorId = author.Id,
                Author = author,
                Title = title,
                MediaType = BookMediaType.Audiobook
            };
        }

        private static DownloadDecision CreateDecision(Author author, Book book, string guid, params Rejection[] rejections)
        {
            return new DownloadDecision(new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                DownloadAllowed = true,
                Release = new TorrentInfo
                {
                    Guid = guid,
                    Title = $"{book.Title} audiobook",
                    IndexerId = IndexerId,
                    Indexer = "Test Indexer",
                    DownloadUrl = "https://indexer.example/download/shared",
                    MagnetUrl = "magnet:?xt=urn:btih:shared"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.MP3)
                }
            }, rejections);
        }

        private static Harness CreateHarness(CancellationToken cancellationToken = default, ICacheManager cacheManager = null, IPrioritizeDownloadDecision prioritizer = null)
        {
            var search = DispatchProxy.Create<ISearchForReleases, SearchServiceProxy>();
            var prioritize = prioritizer ?? DispatchProxy.Create<IPrioritizeDownloadDecision, PrioritizeProxy>();
            var broadcaster = new RecordingBroadcaster();
            var downloads = new RecordingDownloadService();
            cacheManager ??= new CacheManager();
            var driver = new GroupedAuthorSearchDriver(search, prioritize, broadcaster, cacheManager);
            var controller = new ReleaseController(
                rssFetcherAndParser: null,
                releaseSearchService: search,
                downloadDecisionMaker: null,
                prioritizeDownloadDecision: prioritize,
                downloadService: downloads,
                authorService: null,
                bookService: null,
                parsingService: null,
                indexerFactory: null,
                cacheManager: cacheManager,
                logger: LogManager.GetLogger(nameof(ReleaseControllerGroupedSearchFixture)),
                groupedAuthorSearch: driver.Service);

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.QueryString = new QueryString($"?authorId={AuthorId}");
            context.RequestAborted = cancellationToken;
            controller.ControllerContext = new ControllerContext { HttpContext = context };

            return new Harness(controller, (SearchServiceProxy)(object)search, broadcaster, downloads, driver);
        }

        private sealed class Harness
        {
            public Harness(ReleaseController controller, SearchServiceProxy search, RecordingBroadcaster broadcaster, RecordingDownloadService downloads, GroupedAuthorSearchDriver driver)
            {
                Controller = controller;
                Search = search;
                Broadcaster = broadcaster;
                Downloads = downloads;
                Driver = driver;
            }

            public GroupedAuthorSearchDriver Driver { get; }
            public ReleaseController Controller { get; }
            public SearchServiceProxy Search { get; }
            public RecordingBroadcaster Broadcaster { get; }
            public RecordingDownloadService Downloads { get; }
        }

        private delegate Task SearchHandler(
            int authorId,
            bool missingOnly,
            bool userInvokedSearch,
            bool interactiveSearch,
            BookMediaType? mediaType,
            Func<IReadOnlyList<Book>, Task> onSearchStarted,
            Func<Book, BookSearchResult, Exception, Task> onBookCompleted,
            CancellationToken cancellationToken);

        private delegate Task<List<DownloadDecision>> FlatSearchHandler(
            int authorId,
            bool missingOnly,
            bool userInvokedSearch,
            bool interactiveSearch,
            BookMediaType? mediaType);

        private delegate Task<List<DownloadDecision>> CancellableFlatSearchHandler(
            int authorId,
            bool missingOnly,
            bool userInvokedSearch,
            bool interactiveSearch,
            BookMediaType? mediaType,
            CancellationToken cancellationToken);

        private class SearchServiceProxy : DispatchProxy
        {
            public SearchHandler Handler { get; set; }
            public FlatSearchHandler FlatHandler { get; set; }
            public CancellableFlatSearchHandler CancellableFlatHandler { get; set; }
            public int AuthorSearchCalls { get; private set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != nameof(ISearchForReleases.AuthorSearch))
                {
                    throw new NotImplementedException(method.Name);
                }

                AuthorSearchCalls++;
                if (args.Length == 5)
                {
                    return FlatHandler((int)args[0], (bool)args[1], (bool)args[2], (bool)args[3], (BookMediaType?)args[4]);
                }

                if (args.Length == 6)
                {
                    return CancellableFlatHandler((int)args[0], (bool)args[1], (bool)args[2], (bool)args[3], (BookMediaType?)args[4], (CancellationToken)args[5]);
                }

                return Handler(
                    (int)args[0],
                    (bool)args[1],
                    (bool)args[2],
                    (bool)args[3],
                    (BookMediaType?)args[4],
                    (Func<IReadOnlyList<Book>, Task>)args[5],
                    (Func<Book, BookSearchResult, Exception, Task>)args[6],
                    (CancellationToken)args[7]);
            }
        }

        private class PrioritizeProxy : DispatchProxy
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

        private sealed class RecordingBroadcaster : IBroadcastSignalRMessage
        {
            private readonly ConcurrentQueue<SignalRMessage> _messages = new();

            public bool IsConnected => true;
            public IReadOnlyList<SignalRMessage> Messages => _messages.ToArray();
            public Func<SignalRMessage, Task> OnBroadcast { get; set; }

            public Task BroadcastMessage(SignalRMessage message)
            {
                _messages.Enqueue(message);
                return OnBroadcast?.Invoke(message) ?? Task.CompletedTask;
            }
        }

        private sealed class RecordingDownloadService : IDownloadService
        {
            public List<(RemoteBook RemoteBook, int? DownloadClientId)> Reports { get; } = new();

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Reports.Add((remoteBook, downloadClientId));
                return Task.CompletedTask;
            }
        }
    }
}
