using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;

namespace Chaptarr.Core.Test.Indexers
{
    public partial class ReleaseControllerGroupedSearchFixture
    {
        [Test]
        public async Task snapshot_read_never_starts_a_search_and_requires_an_explicit_scope()
        {
            var harness = CreateHarness();
            Assert.That(await harness.Controller.GetReleases(null, AuthorId, mediaType: "audiobook", existing: true), Is.Null);
            Assert.ThrowsAsync<Chaptarr.Http.REST.BadRequestException>(async () => await harness.Controller.GetReleases(null, AuthorId, existing: true));
            Assert.ThrowsAsync<Chaptarr.Http.REST.BadRequestException>(async () => await harness.Controller.GetReleases(101, AuthorId, mediaType: "audiobook", existing: true));
            Assert.That(harness.Search.AuthorSearchCalls, Is.Zero);
            Assert.That(harness.Driver.Commands, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task completed_or_stopped_results_are_retained_for_the_shared_lifetime(bool stop)
        {
            var harness = CreateHarness();
            var book = CreateBook(101, "Mort", CreateAuthor());
            harness.Search.Handler = async (_, _, _, _, _, started, completed, token) =>
            {
                await started(new[] { book });
                await completed(book, new BookSearchResult { Decisions = new List<DownloadDecision> { CreateDecision(book.Author, book, "mort") } }, null);
                var running = harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook);
                Assert.That(running.BookResults[0].Releases, Has.Count.EqualTo(1));
                Assert.That(running.FinishedAt, Is.Null);
                Assert.That(running.ExpiresAt, Is.Null);
                if (stop)
                {
                    Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.True);
                    token.ThrowIfCancellationRequested();
                }
            };
            await GetGroupedResults(harness);
            var snapshot = (AuthorReleaseSearchResponse)await harness.Controller.GetReleases(null, AuthorId, mediaType: "audiobook", existing: true);
            Assert.That(snapshot.IsComplete, Is.True);
            Assert.That(snapshot.IsCancelled, Is.EqualTo(stop));
            Assert.That(snapshot.StartedAt, Is.Not.Null);
            Assert.That(snapshot.ExpiresAt - snapshot.FinishedAt, Is.EqualTo(InteractiveReleaseMapper.CacheLifetime));
            Assert.That(InteractiveReleaseMapper.CacheLifetime, Is.EqualTo(TimeSpan.FromMinutes(30)));
            var row = snapshot.BookResults[0].Releases[0];
            Assert.That(row.ExpiresAt, Is.EqualTo(snapshot.ExpiresAt));
            Assert.That(harness.Search.AuthorSearchCalls, Is.EqualTo(1));
            Assert.That(harness.Controller.StopAuthorSearch(SearchId).IsStopping, Is.False);
            snapshot.BookResults.Clear();
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).BookResults, Has.Count.EqualTo(1));

            var cache = typeof(GroupedAuthorSearchService).GetField("_searches", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(harness.Driver.Service);
            var key = $"{AuthorId}:{BookMediaType.Audiobook}";
            var value = cache.GetType().GetMethod("Find").Invoke(cache, new object[] { key });
            cache.GetType().GetMethod("Set").Invoke(cache, new object[] { key, value, TimeSpan.Zero });
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook), Is.Null);
            Assert.That(harness.Search.AuthorSearchCalls, Is.EqualTo(1));
        }

        [Test]
        public async Task new_search_replaces_finished_snapshot_but_not_the_other_format()
        {
            var harness = CreateHarness();
            harness.Search.Handler = async (_, _, _, _, _, started, _, _) => await started(Array.Empty<Book>());
            await GetGroupedResults(harness);
            var ebook = harness.Driver.Service.Start(AuthorId, BookMediaType.Ebook, Guid.NewGuid().ToString());
            var audio = harness.Driver.Service.Start(AuthorId, BookMediaType.Audiobook, Guid.NewGuid().ToString());
            Assert.That(audio.SearchId, Is.Not.EqualTo(SearchId));
            Assert.That(audio.IsComplete, Is.False);
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Ebook).SearchId, Is.EqualTo(ebook.SearchId));
            Assert.That(harness.Driver.Service.Start(AuthorId, BookMediaType.Audiobook, Guid.NewGuid().ToString()).SearchId, Is.EqualTo(audio.SearchId));
            Assert.That(harness.Driver.Commands, Has.Count.EqualTo(3));
            harness.Controller.StopAuthorSearch(audio.SearchId);
            harness.Controller.StopAuthorSearch(ebook.SearchId);
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook).IsCancelled, Is.True);
            Assert.That(harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Ebook).IsCancelled, Is.True);
        }
    }
}
