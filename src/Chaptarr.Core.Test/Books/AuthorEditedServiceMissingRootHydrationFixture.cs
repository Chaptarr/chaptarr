using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorEditedServiceMissingRootHydrationFixture
    {
        [Test]
        public void should_queue_forced_metadata_hydration_when_author_gains_ebook_root()
        {
            var commandQueue = new RecordingCommandQueue();
            var subject = new AuthorEditedService(commandQueue, CreateBookService(), LogManager.GetCurrentClassLogger());

            subject.Handle(new AuthorEditedEvent(
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 10,
                    AudiobookRootFolderPath = "/library/audiobooks",
                    EbookRootFolderPath = "/library/ebooks"
                },
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 10,
                    AudiobookRootFolderPath = "/library/audiobooks"
                }));

            var command = commandQueue.PushedCommands.Single().Body as RefreshAuthorCommand;

            Assert.That(command, Is.Not.Null);
            Assert.That(command.AuthorId, Is.EqualTo(1));
            Assert.That(command.RefreshMetadata, Is.True);
            Assert.That(command.RescanFolders, Is.False);
            Assert.That(command.ForceRefresh, Is.True);
        }

        [Test]
        public void should_not_force_refresh_when_author_already_had_both_roots()
        {
            var commandQueue = new RecordingCommandQueue();
            var subject = new AuthorEditedService(commandQueue, CreateBookService(), LogManager.GetCurrentClassLogger());

            subject.Handle(new AuthorEditedEvent(
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 10,
                    AudiobookRootFolderPath = "/library/audiobooks",
                    EbookRootFolderPath = "/library/ebooks"
                },
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 10,
                    AudiobookRootFolderPath = "/library/audiobooks-old",
                    EbookRootFolderPath = "/library/ebooks-old"
                }));

            Assert.That(commandQueue.PushedCommands, Is.Empty);
        }

        [Test]
        public void should_force_refresh_when_metadata_profile_changes()
        {
            var commandQueue = new RecordingCommandQueue();
            var subject = new AuthorEditedService(commandQueue, CreateBookService(), LogManager.GetCurrentClassLogger());

            subject.Handle(new AuthorEditedEvent(
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 11,
                    AudiobookRootFolderPath = "/library/audiobooks",
                    EbookRootFolderPath = "/library/ebooks"
                },
                new Author
                {
                    Id = 1,
                    Name = "Martha Wells",
                    MetadataProfileId = 10,
                    AudiobookRootFolderPath = "/library/audiobooks",
                    EbookRootFolderPath = "/library/ebooks"
                }));

            var command = commandQueue.PushedCommands.Single().Body as RefreshAuthorCommand;

            Assert.That(command, Is.Not.Null);
            Assert.That(command.RefreshMetadata, Is.True);
            Assert.That(command.RescanFolders, Is.False);
            Assert.That(command.ForceRefresh, Is.True);
        }

        private static IBookService CreateBookService()
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            return bookService;
        }

        private class BookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IBookService.GetBooksByAuthor) => new List<Book>(),
                    nameof(IBookService.UpdateMany) => null,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<CommandModel> PushedCommands { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                return commands.Select(command => Push(command)).ToList();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                var model = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued
                };

                PushedCommands.Add(model);
                return model;
            }

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
