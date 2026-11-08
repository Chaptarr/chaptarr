using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;

namespace Chaptarr.Core.Test.Indexers
{
    public partial class ReleaseControllerGroupedSearchFixture
    {
        [TestCase(1, "complete")]
        [TestCase(1000, "complete")]
        [TestCase(1000, "cancelled")]
        [TestCase(1000, "failed")]
        public async Task terminal_notice_size_is_independent_of_saved_releases(int rowCount, string outcome)
        {
            var harness = CreateHarness();
            var author = CreateAuthor();
            var book = CreateBook(901, "Mort", author);
            harness.Search.Handler = async (_, _, _, _, _, started, completed, token) =>
            {
                await started(new[] { book });
                await completed(book, new BookSearchResult
                {
                    Decisions = Enumerable.Range(0, rowCount).Select(index => CreateDecision(author, book, "release-" + index)).ToList()
                }, null);
                if (outcome == "failed") throw new InvalidOperationException("Search failed");
                if (outcome == "cancelled")
                {
                    harness.Driver.Service.Stop(Guid.Parse(SearchId));
                    token.ThrowIfCancellationRequested();
                }
            };
            if (outcome == "failed") Assert.ThrowsAsync<InvalidOperationException>(() => GetGroupedResults(harness));
            else await GetGroupedResults(harness);
            var notice = GroupedMessages(harness.Broadcaster).Last();
            var saved = harness.Driver.Service.GetSnapshot(AuthorId, BookMediaType.Audiobook);
            var noticeBytes = Encoding.UTF8.GetByteCount(notice.ToJson());
            var savedBytes = Encoding.UTF8.GetByteCount(saved.ToJson());
            TestContext.Out.WriteLine($"rows={rowCount}, outcome={outcome}, notification={noticeBytes} bytes, saved snapshot={savedBytes} bytes");
            Assert.Multiple(() =>
            {
                Assert.That(notice.BookResults, Is.Empty);
                Assert.That(notice.IsComplete, Is.True);
                Assert.That(notice.IsCancelled, Is.EqualTo(outcome == "cancelled"));
                Assert.That(notice.IsFailed, Is.EqualTo(outcome == "failed"));
                Assert.That(notice.SearchId, Is.EqualTo(saved.SearchId));
                Assert.That(notice.CommandId, Is.EqualTo(saved.CommandId));
                Assert.That(notice.ExpiresAt, Is.EqualTo(saved.ExpiresAt).And.Not.Null);
                Assert.That(saved.BookResults.Single().Releases, Has.Count.EqualTo(rowCount));
                Assert.That(noticeBytes, Is.LessThan(512));
            });
        }
    }
}
