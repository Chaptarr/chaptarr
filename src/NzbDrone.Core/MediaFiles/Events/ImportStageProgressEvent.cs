using System;
using System.Collections.Generic;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class ImportStageProgressEvent : IEvent
    {
        public ImportStageProgressEvent(ImportStage stage, string message, int currentProgress = 0, int totalProgress = 0)
        {
            Stage = stage;
            Message = message;
            CurrentProgress = currentProgress;
            TotalProgress = totalProgress;
        }

        public ImportStage Stage { get; set; }
        public string Message { get; set; }
        public int CurrentProgress { get; set; }
        public int TotalProgress { get; set; }

        // Running counters for overall import progress
        public int ProcessedAuthorFolders { get; set; }  // How many author folders we've processed so far
        public int ProcessedBookFolders { get; set; }  // How many book folders we've processed so far
        public int AuthorsQueued { get; set; }  // Authors added to pending queue
        public int BookUnitsDiscovered { get; set; }  // File groups discovered during scan
        public int AuthorsImported { get; set; }
        public int AuthorsFailed { get; set; }
        public int AuthorsRetrying { get; set; }
        public int FilesImported { get; set; }

        // Current item being processed (for "flying through" effect)
        public string CurrentItemName { get; set; }  // e.g., "J.K. Rowling" or "Harry Potter"
        public string CurrentItemType { get; set; }  // "author", "book", or "file"

        // Canonical matched identifiers for pinning UI to DB values
        public int? MatchedAuthorId { get; set; }

        // Book that was just matched (for "Matched:" display)
        public string CurrentBookMatched { get; set; }  // e.g., "Harry Potter and the Goblet of Fire"
        public string CurrentBookType { get; set; }  // "audiobook" or "ebook"
        // Matched edition identifier to allow consumers to fetch canonical edition
        public int? MatchedEditionId { get; set; }

        // Command tracking for pause/resume functionality
        public int? CommandId { get; set; }  // The ID of the command running this import
        public string CommandStatus { get; set; }  // "started", "paused", etc.

        // Calculate overall progress percentage based on stage completion
        public int OverallProgressPercentage
        {
            get
            {
                var stageWeights = new Dictionary<ImportStage, (int startPercent, int endPercent)>
                {
                    { ImportStage.ScanningFolders, (0, 15) },
                    { ImportStage.DiscoveringAuthors, (15, 25) },
                    { ImportStage.MatchingAuthorsLocally, (25, 40) },
                    { ImportStage.MatchingAuthorsWithMetadata, (40, 60) },
                    { ImportStage.ImportingAuthorsToDatabase, (60, 75) },
                    { ImportStage.MatchingBooks, (75, 95) },
                    { ImportStage.ImportComplete, (95, 100) }
                };

                if (!stageWeights.ContainsKey(Stage))
                    return 0;

                var (startPercent, endPercent) = stageWeights[Stage];

                if (TotalProgress <= 0)
                    return startPercent;

                var stageProgress = Math.Min(1.0, (double)CurrentProgress / TotalProgress);
                return (int)(startPercent + (stageProgress * (endPercent - startPercent)));
            }
        }

        // Overall statistics that persist across stages
        public int TotalAuthorFolders { get; set; }
        public int TotalBookFolders { get; set; }
        public int MatchedAuthors { get; set; }
        public int MatchedBooks { get; set; }
        public int UnmatchedAuthors { get; set; }
        public int UnmatchedBooks { get; set; }
    }

    public enum ImportStage
    {
        ScanningFolders,
        DiscoveringAuthors,
        MatchingAuthorsLocally,
        MatchingAuthorsWithMetadata,
        ImportingAuthorsToDatabase,
        MatchingBooks,
        ImportComplete
    }
}
