using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NLog;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Indexers
{
    public partial class ReleaseControllerGroupedSearchFixture
    {
        private sealed class ProgressTargetProbe : ProgressMessageTarget
        {
            public ProgressTargetProbe(IEventAggregator events, IManageCommandQueue queue) : base(events, queue) { }
            public void Emit(LogEventInfo log) => Write(log);
        }

        private class ProgressQueueProxy : DispatchProxy
        {
            public int Writes { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != nameof(IManageCommandQueue.SetMessage)) throw new NotImplementedException(method.Name);
                Writes++;
                ((CommandModel)args[0]).Message = (string)args[1];
                return null;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void inner_progress_cannot_replace_author_counts_but_toolbar_search_is_unchanged(bool grouped)
        {
            var queue = DispatchProxy.Create<IManageCommandQueue, ProgressQueueProxy>();
            var events = DispatchProxy.Create<IEventAggregator, CommandEventProxy>();
            var model = new CommandModel { Body = grouped
                ? new InteractiveAuthorSearchCommand { AuthorName = "Terry Pratchett", AuthorId = 718, MediaType = BookMediaType.Audiobook, BooksDone = 2, BooksTotal = 55, SendUpdatesToClient = true }
                : new BookSearchCommand { SendUpdatesToClient = true } };
            var log = new LogEventInfo(LogLevel.Info, "NzbDrone.Core.IndexerSearch.ReleaseSearchService", "Searching indexers for Mort");
            log.Properties["Status"] = "Progress";
            try
            {
                ProgressMessageContext.CommandModel = model;
                using var target = new ProgressTargetProbe(events, queue);
                target.Emit(log);
                target.Emit(log);
                Assert.That(((ProgressQueueProxy)(object)queue).Writes, Is.EqualTo(grouped ? 1 : 2), "Inner log lines must not write identical author progress repeatedly");
                Assert.That(model.Message, Is.EqualTo(grouped ? "Searching Terry Pratchett (Audiobooks): 2 of 55 books" : "Searching indexers for Mort"));
            }
            finally { ProgressMessageContext.CommandModel = null; }
        }

        [Test]
        public void exclusive_author_search_uses_one_worker_and_does_not_block_other_command_types()
        {
            var queue = new CommandQueue();
            var first = new CommandModel { Id = 1, Name = "InteractiveAuthorSearch", Body = new InteractiveAuthorSearchCommand(), Status = CommandStatus.Queued, Priority = CommandPriority.Normal };
            var second = new CommandModel { Id = 2, Name = first.Name, Body = new InteractiveAuthorSearchCommand(), Status = CommandStatus.Queued, Priority = CommandPriority.Normal };
            var other = new CommandModel { Id = 3, Name = "BookSearch", Body = new BookSearchCommand(), Status = CommandStatus.Queued, Priority = CommandPriority.Normal };
            queue.Add(first);
            queue.Add(second);
            Assert.That(queue.TryGet(out var taken), Is.True);
            Assert.That(taken, Is.SameAs(first));
            Assert.That(queue.TryGet(out _), Is.False);
            queue.Add(other);
            Assert.That(queue.TryGet(out taken), Is.True);
            Assert.That(taken, Is.SameAs(other));
            first.Status = CommandStatus.Completed;
            Assert.That(queue.TryGet(out taken), Is.True);
            Assert.That(taken, Is.SameAs(second));
        }

        [Test]
        public async Task grouped_rows_share_the_search_lifetime_and_successful_grabs_survive_a_reload()
        {
            var cache = new CacheManager();
            var harness = CreateHarness(cacheManager: cache);
            var book = CreateBook(901, "Mort", CreateAuthor());
            ReleaseResource runningRow = null;
            harness.Search.Handler = async (_, _, _, _, _, started, completed, _) =>
            {
                await started(new[] { book });
                await completed(book, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(book.Author, book, "retained") } }, null);
                runningRow = harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults[0].Releases[0];
                Assert.That(runningRow.ExpiresAt, Is.Null, "A running search has no grab deadline");
                Assert.That(cache.GetCache<DownloadDecision>(typeof(ReleaseController), "downloadDecisions").Count, Is.Zero,
                    "Grouped rows must not have a second, earlier grab-cache deadline");
                await Grab(harness.Controller, runningRow);
                Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults[0].Releases[0].IsGrabbed, Is.True);
            };
            await GetGroupedResults(harness);
            var snapshot = harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook);
            var row = snapshot.BookResults[0].Releases[0];
            Assert.That(row.IsGrabbed, Is.True);
            Assert.That(row.ExpiresAt, Is.EqualTo(snapshot.ExpiresAt));
            Assert.That(snapshot.ExpiresAt - snapshot.FinishedAt, Is.EqualTo(TimeSpan.FromMinutes(30)));
            row.IsGrabbed = false;
            snapshot.BookResults[0].Releases.Clear();
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults[0].Releases[0].IsGrabbed, Is.True,
                "GET returns detached groups and rows");
            harness.Driver.Service.Start(AuthorId, BookMediaType.Audiobook, Guid.NewGuid().ToString());
            Assert.That(harness.Driver.Service.GetDecision(runningRow), Is.Null, "Replacing a finished search retires its grab decisions");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task deleting_an_author_removes_completed_and_queued_searches(bool finish)
        {
            var harness = CreateHarness();
            harness.Search.Handler = async (_, _, _, _, _, started, _, _) => await started(Array.Empty<Book>());
            if (finish) await GetGroupedResults(harness);
            else harness.Driver.Service.Start(AuthorId, BookMediaType.Audiobook, SearchId);
            harness.Driver.Service.HandleAsync(new AuthorDeletedEvent(CreateAuthor(), false, false));
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook), Is.Null);
            Assert.That(harness.Driver.Service.Stop(Guid.Parse(SearchId)).IsStopping, Is.False);
            Assert.That(harness.Search.AuthorSearchCalls, Is.EqualTo(finish ? 1 : 0));
        }
    }
}
