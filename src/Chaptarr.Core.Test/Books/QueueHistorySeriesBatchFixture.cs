using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.History;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class QueueHistorySeriesBatchFixture
    {
        private sealed class NoLazySeriesRead : LazyLoaded<List<SeriesBookLink>>
        {
            public override void LazyLoad() => throw new AssertionException("Resource mapping issued a per-book lazy series read");
        }

        [Test]
        public void included_books_load_series_once_for_only_the_returned_batch(
            [Values(false, true)] bool postgres, [Values(false, true)] bool includeBook,
            [Values("queue", "history", "since", "author", "book")] string route)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Page Writer", CleanName = "pagewriter", SortName = "pagewriter", TitleSlug = "page-writer",
                    Path = "/fixture/page-writer", Added = DateTime.UtcNow
                });
                var bookRows = new BookRepository(database, events);
                var linkRows = new SeriesBookLinkRepository(database, events);
                var series = new SeriesRepository(database, events, linkRows, logger).Insert(new Series
                {
                    Title = "Paging Series", TitleSlug = "paging-series", GoodreadsSeriesId = "gr:7001", MediaType = BookMediaType.Ebook
                });
                var books = Enumerable.Range(0, 6).Select(index =>
                {
                    var stored = bookRows.Insert(new Book
                    {
                        AuthorId = author.Id, Title = "Paged book " + index, TitleSlug = "paged-book-" + index,
                        CleanTitle = "pagedbook" + index, MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow
                    });
                    linkRows.Insert(new SeriesBookLink { BookId = stored.Id, SeriesId = series.Id, Position = index.ToString() });
                    return new Book
                    {
                        Id = stored.Id, AuthorId = author.Id, Author = author, Title = stored.Title, MediaType = BookMediaType.Ebook,
                        Editions = new List<Edition>(), BookFiles = new List<BookFile>(), LazySeriesLinks = new NoLazySeriesRead()
                    };
                }).ToList();
                // Duplicate returned book references must not add duplicate lookup IDs.
                var returnedBooks = new[] { books[0], books[1], books[2], books[3], books[3], books[5] };
                var sql = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                    {
                        var value = connectionMethod.Invoke(connection, connectionArgs);
                        return value is IDbCommand command ? Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            if (commandMethod.Name == nameof(IDbCommand.ExecuteReader)) sql.Add(command.CommandText);
                            return commandMethod.Invoke(command, commandArgs);
                        }) : value;
                    });
                });
                var actualLinks = new SeriesBookLinkService(new SeriesBookLinkRepository(observed, events));
                var batches = new List<List<int>>();
                var links = Proxy<ISeriesBookLinkService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ISeriesBookLinkService.GetLinksByBook)));
                    var ids = (List<int>)args[0];
                    batches.Add(ids.ToList());
                    return actualLinks.GetLinksByBook(ids);
                });
                var paging = new PagingRequestResource { Page = 2, PageSize = 3, SortKey = "title", SortDirection = SortDirection.Ascending };
                List<BookResource> resources;
                IEnumerable<int> expectedIds;
                if (route == "queue")
                {
                    var queue = Proxy<IQueueService>((_, _) => returnedBooks.Select((book, index) => new NzbDrone.Core.Queue.Queue
                    {
                        Id = index + 1, Book = book, Author = author, Title = "Queue " + index, Status = "Downloading"
                    }).ToList());
                    var pending = Proxy<IPendingReleaseService>((_, _) => new List<NzbDrone.Core.Queue.Queue>());
                    var profiles = new QualityProfileService(Proxy<IProfileRepository>((_, _) => new List<QualityProfile>
                    {
                        new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } } }
                    }), null, null, null, null, null, logger);
                    var controller = new QueueController(links, null, null, queue, pending, profiles, null, null, null, null, null, null)
                    {
                        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                    };
                    resources = controller.GetQueue(paging, includeBook: includeBook).Records.Select(resource => resource.Book).ToList();
                    expectedIds = returnedBooks.Skip(3).Select(book => book.Id);
                }
                else
                {
                    var history = returnedBooks.Select((book, index) => new EntityHistory
                    {
                        Id = index + 1, Book = book, BookId = book.Id, Quality = new QualityModel(Quality.EPUB),
                        Date = DateTime.UtcNow, EventType = EntityHistoryEventType.BookFileImported
                    }).ToList();
                    var historyService = Proxy<IHistoryService>((method, args) =>
                    {
                        if (method.Name == nameof(IHistoryService.Paged))
                        {
                            var page = (PagingSpec<EntityHistory>)args[0];
                            page.Records = history.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToList();
                            page.TotalRecords = history.Count;
                            return page;
                        }
                        if (method.Name == nameof(IHistoryService.GetByBook)) return history.Where(item => item.BookId == (int)args[0]).ToList();
                        if (method.Name is nameof(IHistoryService.Since) or nameof(IHistoryService.GetByAuthor)) return history;
                        throw new AssertionException(method.Name);
                    });
                    var controller = new HistoryController(links, historyService,
                        Proxy<ICustomFormatCalculationService>((_, _) => new List<CustomFormat>()),
                        Proxy<IUpgradableSpecification>((_, _) => false), null, Proxy<IAuthorService>((_, _) => author))
                    {
                        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                    };
                    List<HistoryResource> result;
                    if (route == "history") result = controller.GetHistory(paging, false, includeBook, null, null, null).Records;
                    else if (route == "since") result = controller.GetHistorySince(DateTime.UtcNow.AddDays(-1), includeBook: includeBook);
                    else result = controller.GetAuthorHistory(author.Id, route == "book" ? books[3].Id : null, includeBook: includeBook);
                    resources = result.Select(resource => resource.Book).ToList();
                    expectedIds = result.Select(resource => resource.BookId);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(batches.Count, Is.EqualTo(includeBook ? 1 : 0));
                    Assert.That(sql.Count, Is.EqualTo(includeBook ? 2 : 0),
                        "The canonical batch performs one membership query and one series hydration query, independent of row count");
                    if (includeBook)
                    {
                        Assert.That(batches.Single(), Is.EquivalentTo(expectedIds.Distinct()));
                        Assert.That(resources.All(resource => resource.SeriesLabels.Count == 1 && resource.SeriesLabels[0].StartsWith("Paging Series #")), Is.True);
                    }
                    else Assert.That(resources, Is.All.Null);
                });
            });
        }
    }
}
