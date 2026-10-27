using System;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerReleaseDateUpdateFixture
    {
        private sealed class DisconnectedSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;

            public Task BroadcastMessage(SignalRMessage message)
            {
                return Task.CompletedTask;
            }
        }

        [Test]
        public void native_book_put_preserves_book_date_when_resource_projects_monitored_edition_date()
        {
            AssertBookDateIsPreserved(facadeRoute: false);
        }

        [Test]
        public void facade_body_put_outside_monitoring_branch_preserves_book_date_when_resource_projects_edition_date()
        {
            AssertBookDateIsPreserved(facadeRoute: true);
        }

        private static void AssertBookDateIsPreserved(bool facadeRoute)
        {
            var bookDate = new DateTime(2016, 8, 24, 0, 0, 0, DateTimeKind.Utc);
            var monitoredEditionDate = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            var facadeContext = facadeRoute
                ? new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook")
                : null;
            var storedBook = new Book
            {
                Id = 123,
                Author = new Author { Id = 456, Name = "Test Author", SortNameLastFirst = "Author, Test" },
                Title = "Fixture Book",
                MediaType = BookMediaType.Audiobook,
                ReleaseDate = bookDate,
                AnyEditionOk = true,
                AudiobookMonitored = false,
                Editions = new()
                {
                    new Edition
                    {
                        Id = 789,
                        BookId = 123,
                        Title = "Fixture Book",
                        Format = "audiobook",
                        ReadingFormatId = 2,
                        ReleaseDate = monitoredEditionDate,
                        Monitored = true
                    }
                }
            };
            var request = storedBook.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext });

            Assert.Multiple(() =>
            {
                Assert.That(request.ReleaseDate, Is.EqualTo(monitoredEditionDate));
                Assert.That(request.ReleaseDate, Is.Not.EqualTo(bookDate));
                Assert.That(request.Monitored, Is.False);
            });

            DateTime? persistedBookDate = null;
            var bookUpdateCount = 0;
            var bookService = Proxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.GetBook))
                {
                    return storedBook;
                }

                if (method.Name == nameof(IBookService.UpdateBook))
                {
                    bookUpdateCount++;
                    persistedBookDate = ((Book)args[0]).ReleaseDate;
                    return args[0];
                }

                throw new NotImplementedException(method.Name);
            });
            var editionService = Proxy<IEditionService>((method, args) =>
            {
                if (method.Name == nameof(IEditionService.UpdateMany))
                {
                    return null;
                }

                throw new NotImplementedException(method.Name);
            });
            var editionSelector = Proxy<IEditionSelector>((method, args) =>
            {
                if (method.Name == nameof(IEditionSelector.EnsureSingleMonitoredEdition))
                {
                    return false;
                }

                throw new NotImplementedException(method.Name);
            });
            var authorStatisticsService = Proxy<IAuthorStatisticsService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorStatisticsService.AuthorStatistics))
                {
                    return new AuthorStatistics { BookStatistics = new() };
                }

                throw new NotImplementedException(method.Name);
            });
            var coverMapper = Proxy<IMapCoversToLocal>((method, args) =>
            {
                if (method.Name == nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls))
                {
                    return null;
                }

                throw new NotImplementedException(method.Name);
            });
            var controller = new BookController(
                authorService: null,
                bookService: bookService,
                addBookService: null,
                editionService: editionService,
                editionSelector: editionSelector,
                seriesBookLinkService: null,
                authorStatisticsService: authorStatisticsService,
                mediaFileService: null,
                coverMapper: coverMapper,
                upgradableSpecification: null,
                signalRBroadcaster: new DisconnectedSignalRBroadcaster(),
                commandQueueManager: null,
                eventAggregator: null,
                rootFolderService: null,
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                logger: LogManager.GetCurrentClassLogger());
            var httpContext = new DefaultHttpContext();
            if (facadeContext != null)
            {
                httpContext.Items[ReadarrFacadeContext.ItemKey] = facadeContext;
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = facadeRoute
                ? controller.UpdateBookByBody(request)
                : controller.UpdateBook(request);
            var acceptedResult = result.Result as AcceptedAtActionResult;
            var response = acceptedResult?.Value as BookResource;

            Assert.Multiple(() =>
            {
                Assert.That(acceptedResult, Is.Not.Null);
                Assert.That(response?.ReleaseDate, Is.EqualTo(monitoredEditionDate));
                Assert.That(response?.ReleaseDate, Is.Not.EqualTo(bookDate));
                Assert.That(bookUpdateCount, Is.EqualTo(1));
                Assert.That(persistedBookDate, Is.EqualTo(bookDate));
                Assert.That(storedBook.ReleaseDate, Is.EqualTo(bookDate));
            });
        }
    }
}
