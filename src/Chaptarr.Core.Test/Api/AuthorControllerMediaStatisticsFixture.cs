using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerMediaStatisticsFixture
    {
        private const int AuthorId = 12;
        private static readonly string AudiobookRootPath = @"C:\audiobooks".AsOsAgnostic();
        private static readonly string EbookRootPath = @"C:\ebooks".AsOsAgnostic();
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

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void scoped_author_update_should_preserve_the_other_gate_and_skip_author_wide_sync(string mediaType)
        {
            var author = CreateAuthor(syncAcrossFormats: true, audiobookMonitored: false, ebookMonitored: false);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorServiceProxy = (AuthorServiceProxy)(object)authorService;
            authorServiceProxy.Author = author;
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var commandQueueProxy = (CommandQueueProxy)(object)commandQueue;
            var controller = BuildController(authorService, BuildBookService(), commandQueue,
                BuildRootFolderService());
            var facadeContext = new ReadarrFacadeContext("hc", mediaType, $"/readarr/hc/{mediaType}");
            var httpContext = new DefaultHttpContext();
            httpContext.Items[ReadarrFacadeContext.ItemKey] = facadeContext;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            var resource = author.ToResource(facadeContext);
            resource.Monitored = true;

            var result = controller.UpdateAuthor(resource);

            Assert.That(result.Result, Is.TypeOf<AcceptedAtActionResult>());
            Assert.Multiple(() =>
            {
                Assert.That(authorServiceProxy.UpdatedAuthor.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(authorServiceProxy.UpdatedAuthor.AudiobookMonitored, Is.EqualTo(mediaType == "audiobook"));
                Assert.That(authorServiceProxy.UpdatedAuthor.EbookMonitored, Is.EqualTo(mediaType == "ebook"));
                Assert.That(commandQueueProxy.Commands, Is.Empty);
            });
        }

        [Test]
        public void unscoped_author_update_should_keep_author_wide_sync_reconciliation()
        {
            var author = CreateAuthor(syncAcrossFormats: false, audiobookMonitored: true, ebookMonitored: true);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorServiceProxy = (AuthorServiceProxy)(object)authorService;
            authorServiceProxy.Author = author;
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var commandQueueProxy = (CommandQueueProxy)(object)commandQueue;
            var controller = BuildController(authorService, BuildBookService(), commandQueue,
                BuildRootFolderService());
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = controller.UpdateAuthor(new AuthorResource
            {
                Id = AuthorId,
                AuthorName = author.Name,
                ForeignAuthorId = "hc:123",
                Monitored = true,
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookRootFolderPath = AudiobookRootPath,
                EbookRootFolderPath = EbookRootPath,
                SyncMonitoredAcrossFormats = true
            });

            Assert.That(result.Result, Is.TypeOf<AcceptedAtActionResult>());
            Assert.Multiple(() =>
            {
                Assert.That(authorServiceProxy.UpdatedAuthor.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(authorServiceProxy.UpdatedAuthor.EbookMonitored, Is.True);
                Assert.That(authorServiceProxy.UpdatedAuthor.AudiobookMonitored, Is.True);
                Assert.That(commandQueueProxy.Commands, Has.Count.EqualTo(1));
                Assert.That(commandQueueProxy.Commands[0], Is.TypeOf<BulkSyncFormatMonitoringCommand>());
                Assert.That(((BulkSyncFormatMonitoringCommand)commandQueueProxy.Commands[0]).AuthorIds,
                    Is.EqualTo(new[] { AuthorId }));
            });
        }

        [Test]
        public void explicit_folder_put_queues_only_the_chosen_format_and_honors_shared_moves(
            [Values("audiobook", "ebook")] string media, [Values(false, true)] bool shared, [Values(false, true)] bool moveFiles)
        {
            var author = CreateAuthor(false, true, true);
            author.AudiobookPath = Path.Combine(AudiobookRootPath, "library", author.Name);
            author.EbookPath = shared ? author.AudiobookPath : Path.Combine(EbookRootPath, "library", author.Name);
            author.Path = author.AudiobookPath;
            if (shared) author.EbookRootFolderPath = AudiobookRootPath;
            var previousAudio = author.AudiobookPath;
            var previousEbook = author.EbookPath;
            var service = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)service).Author = author;
            var queue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var roots = BuildRootFolderService();
            if (shared) ((RootFolderServiceProxy)(object)roots).RootFolders[0].FolderType = FolderType.Mixed;
            var controller = BuildController(service, BuildBookService(), queue, roots);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var resource = author.ToResource();
            resource.AudiobookFolder = null;
            resource.EbookFolder = null;
            var target = Path.Combine(media == "audiobook" || shared ? AudiobookRootPath : EbookRootPath, author.Name);
            if (media == "audiobook") resource.AudiobookFolder = target;
            else resource.EbookFolder = target;
            Assert.That(controller.UpdateAuthor(resource, moveFiles).Result, Is.TypeOf<AcceptedAtActionResult>());
            var saved = ((AuthorServiceProxy)(object)service).UpdatedAuthor;
            Assert.That(saved.AudiobookPath, Is.EqualTo(media == "audiobook" || (shared && moveFiles) ? target : previousAudio));
            Assert.That(saved.EbookPath, Is.EqualTo(media == "ebook" || (shared && moveFiles) ? target : previousEbook));
            var commands = ((CommandQueueProxy)(object)queue).Commands;
            Assert.That(commands.Count, Is.EqualTo(moveFiles ? 1 : 0));
            if (moveFiles)
            {
                var move = (MoveAuthorCommand)commands.Single();
                Assert.That(move.DestinationPath, Is.EqualTo(target));
                Assert.That(move.SharedFolder, Is.EqualTo(shared));
                Assert.That(move.MediaType, Is.EqualTo(shared ? "audiobook" : media));
            }
        }

        private static Author CreateAuthor(bool syncAcrossFormats, bool audiobookMonitored, bool ebookMonitored)
        {
            return new Author
            {
                Id = AuthorId,
                Name = "Test Author",
                NameLastFirst = "Author, Test",
                HardcoverAuthorId = "hc:123",
                AudiobookMonitored = audiobookMonitored,
                EbookMonitored = ebookMonitored,
                SyncMonitoredAcrossFormats = syncAcrossFormats,
                AudiobookRootFolderPath = AudiobookRootPath,
                EbookRootFolderPath = EbookRootPath
            };
        }

        private static IBookService BuildBookService()
        {
            return DispatchProxy.Create<IBookService, BookServiceProxy>();
        }

        private static IRootFolderService BuildRootFolderService()
        {
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolders.AddRange(new[]
            {
                new RootFolder { Path = AudiobookRootPath, FolderType = FolderType.Audiobook },
                new RootFolder { Path = EbookRootPath, FolderType = FolderType.Ebook }
            });
            return rootFolderService;
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

        private static AuthorController BuildController(
            IAuthorService authorService,
            IBookService bookService,
            IManageCommandQueue commandQueue = null,
            IRootFolderService rootFolderService = null)
        {
            var statisticsService = DispatchProxy.Create<IAuthorStatisticsService, AuthorStatisticsServiceProxy>();
            return new AuthorController(
                signalRBroadcaster: DispatchProxy.Create<IBroadcastSignalRMessage, SignalRProxy>(),
                authorService: authorService,
                bookService: bookService,
                bookMonitoredService: null,
                authorLibraryService: null,
                authorStatisticsService: statisticsService,
                coverMapper: DispatchProxy.Create<IMapCoversToLocal, CoversToLocalProxy>(),
                commandQueueManager: commandQueue,
                rootFolderService: rootFolderService,
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

                if (targetMethod?.Name == nameof(IAuthorService.UpdateAuthor))
                {
                    UpdatedAuthor = (Author)args[0];
                    return UpdatedAuthor;
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }

            public Author UpdatedAuthor { get; private set; }
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

        private class CommandQueueProxy : DispatchProxy
        {
            public List<Command> Commands { get; } = new List<Command>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.PushMany))
                {
                    Commands.AddRange((IEnumerable<Command>)args[0]);
                    return new List<CommandModel>();
                }

                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) && args?[0] is Command command)
                {
                    Commands.Add(command);
                    return new CommandModel { Body = command, Name = command.Name, Status = CommandStatus.Queued };
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; } = new List<RootFolder>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return RootFolders;
                }

                throw new System.NotImplementedException(targetMethod?.Name);
            }
        }

        private class SignalRProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_IsConnected")
                {
                    return false;
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
