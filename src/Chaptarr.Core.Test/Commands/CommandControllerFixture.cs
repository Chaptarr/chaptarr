using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandControllerFixture
    {
        private sealed class StubCommandQueue : IManageCommandQueue
        {
            private CommandModel _pushed;

            public bool WasPushed => _pushed != null;
            public Command PushedCommand => _pushed?.Body as Command;

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
                where TCommand : Command => throw new NotImplementedException();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                _pushed = new CommandModel
                {
                    Id = 1,
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued,
                    QueuedAt = DateTime.UtcNow
                };

                return _pushed;
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => new List<CommandModel>();
            public CommandModel Get(int id) => _pushed;
            public List<CommandModel> GetStarted() => new List<CommandModel>();
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
            public CancellationToken GetCancellationToken(int commandId) => CancellationToken.None;
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private sealed class StubSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        [Test]
        public void rename_author_should_reject_unknown_media_type_before_queueing()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"RenameAuthor","authorIds":[1],"mediaType":"audio"}""");

            var exception = Assert.Throws<BadRequestException>(() => controller.StartCommand(new CommandResource { Name = "RenameAuthor" }));

            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
            Assert.That(queue.WasPushed, Is.False);
        }

        [Test]
        public void rename_author_should_accept_all_media_type()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"RenameAuthor","authorIds":[1],"mediaType":"all"}""");

            var result = controller.StartCommand(new CommandResource { Name = "RenameAuthor" });

            Assert.That(queue.WasPushed, Is.True);
            Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
        }

        [Test]
        public void missing_book_search_should_reject_numeric_media_type_before_queueing()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"MissingBookSearch","mediaType":"0"}""");

            var exception = Assert.Throws<BadRequestException>(() => controller.StartCommand(new CommandResource { Name = "MissingBookSearch" }));

            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
            Assert.That(queue.WasPushed, Is.False);
        }

        [Test]
        public void missing_book_search_should_normalize_all_media_type_to_unfiltered()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"MissingBookSearch","mediaType":"ALL"}""");

            var result = controller.StartCommand(new CommandResource { Name = "MissingBookSearch" });

            Assert.That(queue.WasPushed, Is.True);
            Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
            Assert.That(((MissingBookSearchCommand)queue.PushedCommand).MediaType, Is.Null);
        }

        private static CommandController CreateController(StubCommandQueue queue, string body)
        {
            var controller = new CommandController(
                queue,
                new StubSignalRBroadcaster(),
                new KnownTypes(new List<Type>
                {
                    typeof(RenameAuthorCommand),
                    typeof(MissingBookSearchCommand),
                    typeof(CutoffUnmetBookSearchCommand)
                }),
                LogManager.GetCurrentClassLogger());

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = context
            };

            return controller;
        }
    }
}
