using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class SendMatchingLogsCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => false;
        public override bool IsExclusive => false;
        public override bool IsTypeExclusive => true; // Prevent multiple log uploads running simultaneously

        /// <summary>
        /// Progress message for UI updates
        /// </summary>
        public string ProgressMessage { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of log entries to include (for performance)
        /// </summary>
        public int MaxEntries { get; set; } = 1000;

        /// <summary>
        /// Filter to only failed matches (reduces log size)
        /// </summary>
        public bool FailedMatchesOnly { get; set; } = true;

        /// <summary>
        /// Days of logs to include (default: last 7 days)
        /// </summary>
        public int DaysBack { get; set; } = 7;

        /// <summary>
        /// Minutes of logs to include (takes precedence over DaysBack if specified)
        /// </summary>
        public int? MinutesBack { get; set; }

        /// <summary>
        /// Optional file paths to include in the upload (for targeted analysis)
        /// </summary>
        public List<string> SpecificFilePaths { get; set; } = new List<string>();

        /// <summary>
        /// Optional media type filter for server-side unmapped file selection.
        /// </summary>
        public string MediaType { get; set; }

        /// <summary>
        /// Optional server-side selector for unmapped file log scoping.
        /// </summary>
        public UnmappedFilesSelection UnmappedFiles { get; set; }

        public SendMatchingLogsCommand()
        {
        }

        public SendMatchingLogsCommand(int maxEntries = 1000, bool failedMatchesOnly = true, int daysBack = 7)
        {
            MaxEntries = maxEntries;
            FailedMatchesOnly = failedMatchesOnly;
            DaysBack = daysBack;
        }
    }
}
