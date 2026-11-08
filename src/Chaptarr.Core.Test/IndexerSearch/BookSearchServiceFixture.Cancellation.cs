using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.IndexerSearch
{
    public partial class BookSearchServiceFixture
    {
        [Test]
        public void deleted_selected_book_does_not_cancel_other_selected_searches()
        {
            var books = SelectedBooks();
            var proxy = (BookServiceProxy)(object)books;
            var search = new RecordingReleaseSearch();
            var messages = new MemoryTarget { Layout = "${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            using var logs = new LogFactory { Configuration = config };
            var subject = new BookSearchService(search, books, null, null, new EmptyDecisionProcessor(), logs.GetLogger("SelectedSearch"));

            subject.Execute(new BookSearchCommand(new List<int> { 5793, 9999, 5792 }) { Trigger = CommandTrigger.Manual });

            Assert.That(search.BookSearchIds, Is.EqualTo(new[] { 5793, 5792 }));
            Assert.That(proxy.SelectedBookRequests, Is.EqualTo(1));
            Assert.That(proxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10 }));
            Assert.That(search.AllowUnmonitoredValues, Is.All.True, "Explicitly selected books allow the one-time search");
            Assert.That(messages.Logs, Has.One.Contains("Skipping selected book 9999"));
        }

        [Test]
        public void selected_books_should_reuse_catalogs_without_reordering_interleaved_authors()
        {
            var books = SelectedBooks();
            var proxy = (BookServiceProxy)(object)books;
            var secondAuthor = new Author { Id = 20, EbookQualityProfileId = 2 };
            proxy.AuthorBooks[20] = Enumerable.Range(6000, 100).Select(id => new Book
            {
                Id = id, AuthorId = 20, Author = secondAuthor, MediaType = BookMediaType.Ebook
            }).ToList();
            var selected = new[] { 5793, 6002, 5792 }.Concat(Enumerable.Range(6000, 100)).Distinct().ToList();
            var search = new RecordingReleaseSearch();
            var subject = new BookSearchService(search, books, null, null, new EmptyDecisionProcessor(), LogManager.GetCurrentClassLogger());

            subject.Execute(new BookSearchCommand(selected) { Trigger = CommandTrigger.Manual });

            Assert.Multiple(() =>
            {
                Assert.That(proxy.SelectedBookRequests, Is.EqualTo(1));
                Assert.That(proxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10, 20 }));
                Assert.That(search.BookSearchIds, Is.EqualTo(selected));
                Assert.That(search.AuthorCatalogSizes.Take(3), Is.EqualTo(new[] { 2, 100, 2 }));
                Assert.That(search.AuthorCatalogSizes.Skip(3), Is.All.EqualTo(100));
                Assert.That(search.AllowUnmonitoredValues, Is.All.True, "Explicitly selected books allow the one-time search");
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void cancelled_toolbar_search_stops_before_grabbing_or_before_the_next_book(bool duringSearch)
        {
            var books = SelectedBooks();
            foreach (var book in ((BookServiceProxy)(object)books).AuthorBooks[10])
            {
                book.AudiobookMonitored = true;
                book.Author.AudiobookMonitored = true;
                book.Author.Name = "Terry Pratchett";
            }

            using var cancellation = new CancellationTokenSource();
            var search = new RecordingReleaseSearch
            {
                OnBookSearch = _ => { if (duringSearch) cancellation.Cancel(); }
            };
            var processed = 0;
            var processor = new EmptyDecisionProcessor { OnProcess = () => { processed++; cancellation.Cancel(); } };
            var messages = new MemoryTarget { Layout = "${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Info, LogLevel.Fatal, messages);
            using var logs = new LogFactory { Configuration = config };
            var subject = new BookSearchService(search, books, null, null, processor, logs.GetLogger("Cancellation"));

            Assert.Throws<OperationCanceledException>(() => subject.Execute(new AuthorSearchCommand { AuthorId = 10 }, cancellation.Token));

            Assert.Multiple(() =>
            {
                Assert.That(search.BookSearchIds, Is.EqualTo(new[] { 5792 }));
                Assert.That(processed, Is.EqualTo(duringSearch ? 0 : 1));
                Assert.That(messages.Logs, Has.Some.Contains("Searching 2 monitored books for Terry Pratchett (author 10)"));
                Assert.That(messages.Logs.Count(line => line.Contains("cancelled after")), Is.EqualTo(1));
                Assert.That(messages.Logs.Last(), Does.Contain($"after {(duringSearch ? 0 : 1)} of 2 books").And.Contain("0 grabbed"));
            });
        }
    }
}
