using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RetryUnmappedFilesCommandHandler : IExecute<RetryUnmappedFilesCommand>
    {
        private readonly IExecute<RetryUnmappedMatchCommand> _retryUnmappedMatch;

        public RetryUnmappedFilesCommandHandler(IExecute<RetryUnmappedMatchCommand> retryUnmappedMatch)
        {
            _retryUnmappedMatch = retryUnmappedMatch;
        }

        public void Execute(RetryUnmappedFilesCommand message)
        {
            _retryUnmappedMatch.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "all",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });
        }
    }
}
