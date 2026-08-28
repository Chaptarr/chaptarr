using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerPurgeAndReaddFixture
    {
        [Test]
        public async Task purge_and_readd_should_retry_only_retained_unmapped_book_file_rows()
        {
            var original = new Author
            {
                Id = 7,
                Name = "Frank Herbert",
                HardcoverAuthorId = "hc:123",
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                Tags = new HashSet<int>(),
                AudiobookTags = new HashSet<int>(),
                EbookTags = new HashSet<int>()
            };
            var generated = new Author
            {
                Id = 22,
                Name = original.Name,
                AudiobookRootFolderPath = original.AudiobookRootFolderPath,
                EbookRootFolderPath = original.EbookRootFolderPath
            };
            var retainedBookFileIds = new List<int> { 41, 42, 43 };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorProxy = (AuthorServiceProxy)(object)authorService;
            authorProxy.Original = original;
            authorProxy.RetainedBookFileIds = retainedBookFileIds;

            var libraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            var libraryProxy = (AuthorLibraryServiceProxy)(object)libraryService;
            libraryProxy.PreflightResult = original;
            libraryProxy.ReaddResult = generated;

            var commandQueue = new RecordingCommandQueue();
            var controller = new AuthorController(
                signalRBroadcaster: null,
                authorService: authorService,
                bookService: null,
                bookMonitoredService: null,
                seriesService: null,
                authorLibraryService: libraryService,
                authorStatisticsService: null,
                coverMapper: null,
                commandQueueManager: commandQueue,
                rootFolderService: null,
                eventAggregator: null,
                appFolderInfo: null,
                fileNameBuilder: null,
                logger: LogManager.GetLogger(nameof(AuthorControllerPurgeAndReaddFixture)),
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

            var result = await controller.DeleteAuthor(original.Id, readdAuthor: true);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.TypeOf<OkResult>());
                Assert.That(libraryProxy.AddCalls, Is.EqualTo(2));
                Assert.That(authorProxy.DeleteForReaddCalls, Is.EqualTo(1));
                Assert.That(commandQueue.Pushed, Has.Count.EqualTo(1));
            });

            var queued = commandQueue.Pushed.Single();
            var retry = queued.Body as RetryUnmappedMatchCommand;
            Assert.Multiple(() =>
            {
                Assert.That(retry, Is.Not.Null);
                Assert.That(retry.MediaType, Is.EqualTo("all"));
                Assert.That(retry.UnmappedFiles.Scope, Is.EqualTo("selected"));
                Assert.That(retry.UnmappedFiles.BookFileIds, Is.EquivalentTo(retainedBookFileIds));
                Assert.That(queued.Trigger, Is.EqualTo(CommandTrigger.Manual));
            });
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public Author Original { get; set; }
            public List<int> RetainedBookFileIds { get; set; } = new List<int>();
            public int DeleteForReaddCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.GetAuthor):
                        return Original;
                    case nameof(IAuthorService.DeleteAuthorForReadd):
                        DeleteForReaddCalls++;
                        return RetainedBookFileIds;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
                }
            }
        }

        public class AuthorLibraryServiceProxy : DispatchProxy
        {
            public Author PreflightResult { get; set; }
            public Author ReaddResult { get; set; }
            public int AddCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    AddCalls++;
                    return Task.FromResult(AddCalls == 1 ? PreflightResult : ReaddResult);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<CommandModel> Pushed { get; } = new List<CommandModel>();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                var model = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger
                };
                Pushed.Add(model);
                return model;
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
