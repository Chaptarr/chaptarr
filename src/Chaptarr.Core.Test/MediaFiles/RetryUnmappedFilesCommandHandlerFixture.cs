using NUnit.Framework;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RetryUnmappedFilesCommandHandlerFixture
    {
        [Test]
        public void should_route_legacy_retry_command_to_all_format_match_retry()
        {
            var retry = new RecordingRetryCommand();
            var handler = new RetryUnmappedFilesCommandHandler(retry);

            handler.Execute(new RetryUnmappedFilesCommand());

            Assert.That(retry.Command, Is.Not.Null);
            Assert.That(retry.Command.MediaType, Is.EqualTo("all"));
            Assert.That(retry.Command.UnmappedFiles.Scope, Is.EqualTo("all"));
        }

        [Test]
        public void should_keep_legacy_command_exclusive_and_disk_bound()
        {
            var command = new RetryUnmappedFilesCommand();

            Assert.That(command.IsTypeExclusive, Is.True);
            Assert.That(command.RequiresDiskAccess, Is.True);
            Assert.That(command.SendUpdatesToClient, Is.True);
        }

        private sealed class RecordingRetryCommand : IExecute<RetryUnmappedMatchCommand>
        {
            public RetryUnmappedMatchCommand Command { get; private set; }

            public void Execute(RetryUnmappedMatchCommand message)
            {
                Command = message;
            }
        }
    }
}
