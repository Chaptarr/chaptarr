using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerMediaStatisticsFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void get_author_resource_should_leave_shared_relationships_unloaded(bool useFacade)
        {
            const int authorId = 12;
            var author = new Author
            {
                Id = authorId,
                Name = "Test Author",
                NameLastFirst = "Author, Test",
                HardcoverAuthorId = "hc:123",
                AudiobookMonitored = true,
                EbookMonitored = true
            };
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)(object)bookService;
            bookServiceProxy.NextBooks.Add(new Book { Id = 21, AuthorId = authorId, Title = "Next Book" });
            bookServiceProxy.LastBooks.Add(new Book { Id = 20, AuthorId = authorId, Title = "Last Book" });

            var controller = BuildController(authorService, bookService);

            var httpContext = new DefaultHttpContext();
            if (useFacade)
            {
                httpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook");
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var resource = controller.GetAuthorById(authorId).Value;

            Assert.Multiple(() =>
            {
                Assert.That(resource, Is.Not.Null);
                Assert.That(resource.AuthorName, Is.EqualTo("Test Author"));
                Assert.That(resource.ForeignAuthorId, Is.EqualTo(useFacade ? "123" : "hc:123"));
                Assert.That(resource.Statistics.BookCount, Is.EqualTo(useFacade ? 4 : 10));
                Assert.That(resource.AudiobookStatistics.BookCount, Is.EqualTo(6));
                Assert.That(resource.EbookStatistics.BookCount, Is.EqualTo(4));
                Assert.That(resource.NextBook.Id, Is.EqualTo(21));
                Assert.That(resource.NextBook.Title, Is.EqualTo("Next Book"));
                Assert.That(resource.LastBook.Id, Is.EqualTo(20));
                Assert.That(resource.LastBook.Title, Is.EqualTo("Last Book"));
                Assert.That(author.Books, Is.Null);
                Assert.That(author.Series, Is.Null);
                Assert.That(bookServiceProxy.GetBooksByAuthorCalls, Is.Zero);
            });

            var jsonWithoutRelationships = STJson.ToJson(resource);
            author.Books = new List<Book> { new Book { Id = 22, Title = "Catalog Book" } };
            author.Series = new List<Series> { new Series { Id = 7, Title = "Catalog Series" } };

            Assert.That(STJson.ToJson(controller.GetAuthorById(authorId).Value), Is.EqualTo(jsonWithoutRelationships));
        }

        [Test]
        public void unscoped_author_list_should_expose_each_media_side_and_their_combined_totals()
        {
            var resources = new List<AuthorResource>
            {
                new AuthorResource { Id = 1 },
                new AuthorResource { Id = 2 }
            };
            var audiobookStatistics = new Dictionary<int, AuthorStatistics>
            {
                [1] = Statistics(authorId: 1, books: 720, files: 0, size: 0)
            };
            var ebookStatistics = new Dictionary<int, AuthorStatistics>
            {
                [1] = Statistics(authorId: 1, books: 566, files: 3, size: 5061609),
                [2] = Statistics(authorId: 2, books: 5, files: 1, size: 100)
            };

            AuthorController.LinkMediaTypeAuthorStatistics(resources, audiobookStatistics, ebookStatistics);

            Assert.Multiple(() =>
            {
                Assert.That(resources[0].AudiobookStatistics.BookCount, Is.EqualTo(720));
                Assert.That(resources[0].AudiobookStatistics.BookFileCount, Is.Zero);
                Assert.That(resources[0].EbookStatistics.BookCount, Is.EqualTo(566));
                Assert.That(resources[0].EbookStatistics.BookFileCount, Is.EqualTo(3));
                Assert.That(resources[0].Statistics.BookCount, Is.EqualTo(1286));
                Assert.That(resources[0].Statistics.BookFileCount, Is.EqualTo(3));
                Assert.That(resources[0].Statistics.SizeOnDisk, Is.EqualTo(5061609));

                Assert.That(resources[1].AudiobookStatistics, Is.Not.Null);
                Assert.That(resources[1].AudiobookStatistics.BookCount, Is.Zero,
                    "A missing media side must be explicit zeroes instead of falling back to combined statistics");
                Assert.That(resources[1].Statistics.BookCount, Is.EqualTo(5));
            });
        }

        private static AuthorStatistics Statistics(int authorId, int books, int files, long size)
        {
            return new AuthorStatistics
            {
                AuthorId = authorId,
                BookCount = books,
                AvailableBookCount = files > 0 ? 1 : 0,
                TotalBookCount = books,
                BookFileCount = files,
                SizeOnDisk = size
            };
        }

        private static AuthorController BuildController(IAuthorService authorService, IBookService bookService)
        {
            var statisticsService = DispatchProxy.Create<IAuthorStatisticsService, AuthorStatisticsServiceProxy>();
            return new AuthorController(
                signalRBroadcaster: null,
                authorService: authorService,
                bookService: bookService,
                bookMonitoredService: null,
                authorLibraryService: null,
                authorStatisticsService: statisticsService,
                coverMapper: DispatchProxy.Create<IMapCoversToLocal, CoversToLocalProxy>(),
                commandQueueManager: null,
                rootFolderService: null,
                eventAggregator: null,
                appFolderInfo: null,
                fileNameBuilder: DispatchProxy.Create<IBuildFileNames, FileNameBuilderProxy>(),
                logger: LogManager.GetLogger(nameof(AuthorControllerMediaStatisticsFixture)),
                recycleBinValidator: new RecycleBinValidator(null),
                rootFolderValidator: new RootFolderValidator(null),
                mappedNetworkDriveValidator: new MappedNetworkDriveValidator(null, null),
                authorPathValidator: new AuthorPathValidator(authorService),
                authorExistsValidator: new AuthorExistsValidator(authorService),
                authorAncestorValidator: new AuthorAncestorValidator(authorService),
                systemFolderValidator: new SystemFolderValidator(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                authorFolderAsRootFolderValidator: new AuthorFolderAsRootFolderValidator(null));
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> NextBooks { get; } = new List<Book>();
            public List<Book> LastBooks { get; } = new List<Book>();
            public int GetBooksByAuthorCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        GetBooksByAuthorCalls++;
                        return new List<Book>();
                    case nameof(IBookService.GetNextBooksByAuthorId):
                        return NextBooks;
                    case nameof(IBookService.GetLastBooksByAuthorId):
                        return LastBooks;
                    default:
                        throw new System.NotImplementedException(targetMethod?.Name);
                }
            }
        }

        private class AuthorStatisticsServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name != nameof(IAuthorStatisticsService.AuthorStatistics))
                {
                    throw new System.NotImplementedException(targetMethod?.Name);
                }

                var authorId = (int)args[0];
                if (args.Length == 1)
                {
                    return Statistics(authorId, books: 10, files: 3, size: 300);
                }

                return (string)args[1] == "audiobook"
                    ? Statistics(authorId, books: 6, files: 2, size: 200)
                    : Statistics(authorId, books: 4, files: 1, size: 100);
            }
        }

        private class CoversToLocalProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMapCoversToLocal.ConvertToLocalUrls))
                {
                    return null;
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }
        }

        private class FileNameBuilderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBuildFileNames.GetAuthorFolder))
                {
                    return "Test Author";
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }
        }
    }
}
