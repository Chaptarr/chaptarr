using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Books;

namespace NzbDrone.SignalR
{
    public class ProgressMessageConnection : IHandle<ImportSummaryEvent>,
                                            IHandle<ImportStageProgressEvent>
    {
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;
        private readonly IAuthorService _authorService;
        private readonly object _authorCacheLock = new object();
        // Pin the canonical author by command: AuthorId + Name from DB
        private readonly System.Collections.Generic.Dictionary<int, (int AuthorId, string Name)> _pinnedAuthorByCommand = new System.Collections.Generic.Dictionary<int, (int AuthorId, string Name)>();

        // Deduplicate ImportStageProgress broadcasts per command to avoid no-op updates
        private readonly object _progressCacheLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, ProgressSnapshot> _lastProgressByCommand = new System.Collections.Generic.Dictionary<int, ProgressSnapshot>();

        private sealed class ProgressSnapshot
        {
            public string Stage { get; set; }
            public int CurrentProgress { get; set; }
            public int TotalProgress { get; set; }
            public int OverallProgressPercentage { get; set; }
            public int TotalAuthorFolders { get; set; }
            public int TotalBookFolders { get; set; }
            public int MatchedAuthors { get; set; }
            public int MatchedBooks { get; set; }
            public int UnmatchedAuthors { get; set; }
            public int UnmatchedBooks { get; set; }
            public int ProcessedAuthorFolders { get; set; }
            public int ProcessedBookFolders { get; set; }
            public int AuthorsImported { get; set; }
            public int FilesImported { get; set; }
            public string CurrentItemName { get; set; }
            public string CurrentItemType { get; set; }
            public string CurrentBookMatched { get; set; }
            public string CurrentBookType { get; set; }

            public bool Equals(ProgressSnapshot other)
            {
                if (other == null) return false;
                return Stage == other.Stage
                    && CurrentProgress == other.CurrentProgress
                    && TotalProgress == other.TotalProgress
                    && OverallProgressPercentage == other.OverallProgressPercentage
                    && TotalAuthorFolders == other.TotalAuthorFolders
                    && TotalBookFolders == other.TotalBookFolders
                    && MatchedAuthors == other.MatchedAuthors
                    && MatchedBooks == other.MatchedBooks
                    && UnmatchedAuthors == other.UnmatchedAuthors
                    && UnmatchedBooks == other.UnmatchedBooks
                    && ProcessedAuthorFolders == other.ProcessedAuthorFolders
                    && ProcessedBookFolders == other.ProcessedBookFolders
                    && AuthorsImported == other.AuthorsImported
                    && FilesImported == other.FilesImported
                    && string.Equals(CurrentItemName ?? string.Empty, other.CurrentItemName ?? string.Empty, System.StringComparison.Ordinal)
                    && string.Equals(CurrentItemType ?? string.Empty, other.CurrentItemType ?? string.Empty, System.StringComparison.Ordinal)
                    && string.Equals(CurrentBookMatched ?? string.Empty, other.CurrentBookMatched ?? string.Empty, System.StringComparison.Ordinal)
                    && string.Equals(CurrentBookType ?? string.Empty, other.CurrentBookType ?? string.Empty, System.StringComparison.Ordinal);
            }
        }

        public ProgressMessageConnection(IBroadcastSignalRMessage signalRBroadcaster, IAuthorService authorService)
        {
            _signalRBroadcaster = signalRBroadcaster;
            _authorService = authorService;
        }

        public void Handle(ImportSummaryEvent message)
        {
            var failedMessage = message.FailedAuthors.Count > 0
                ? $" ({message.FailedAuthors.Count} authors failed)"
                : "";

            var summaryMessage = $"Import complete: Added {message.TotalAuthorsAdded} new authors and matched {message.TotalBooksMatched} of {message.TotalBooksProcessed} books{failedMessage}";

            _signalRBroadcaster.BroadcastMessage(new SignalRMessage
            {
                Name = "ImportSummary",
                Body = new
                {
                    message.FolderPath,
                    message.TotalAuthorsAdded,
                    message.TotalBooksMatched,
                    message.TotalBooksProcessed,
                    message.FailedAuthors,
                    message.ElapsedMilliseconds,
                    Message = summaryMessage
                }
            });

            // Reset pinned authors for all commands after a summary (end of session)
            lock (_authorCacheLock)
            {
                _pinnedAuthorByCommand.Clear();
            }
        }

        public void Handle(ImportStageProgressEvent message)
        {
            // Pin author by AuthorId per CommandId and ALWAYS display DB canonical name
            string currentAuthor = null;
            int? cmdId = message.CommandId;
            if (cmdId.HasValue)
            {
                lock (_authorCacheLock)
                {
                    // If a MatchedAuthorId is present, pin it from DB and use canonical name
                    if (message.MatchedAuthorId.HasValue)
                    {
                        var author = _authorService.GetAuthor(message.MatchedAuthorId.Value);
                        if (author != null)
                        {
                            _pinnedAuthorByCommand[cmdId.Value] = (author.Id, author.Name);
                        }
                    }

                    if (_pinnedAuthorByCommand.TryGetValue(cmdId.Value, out var pinned))
                    {
                        currentAuthor = pinned.Name;
                    }
                }
            }

            // Clear per-command cache on completion
            if (message.Stage == ImportStage.ImportComplete && cmdId.HasValue)
            {
                lock (_authorCacheLock)
                {
                    _pinnedAuthorByCommand.Remove(cmdId.Value);
                }
            }

            var currentItemType = message.CurrentItemType;
            if (string.IsNullOrWhiteSpace(currentItemType) && !string.IsNullOrWhiteSpace(currentAuthor))
            {
                currentItemType = "author";
            }

            // Build snapshot and deduplicate against last broadcast per command.
            // Multiple publishers emit partial ImportStageProgressEvent payloads; merge missing values
            // from the last snapshot for this command and enforce monotonic progress to keep the UI stable.
            var key = cmdId ?? -1; // -1 groups events without command id
            ProgressSnapshot snapshot;
            int overallProgressPercentage;
            int totalAuthorFolders;
            int totalBookFolders;
            int matchedAuthors;
            int matchedBooks;
            int unmatchedAuthors;
            int unmatchedBooks;
            int processedAuthorFolders;
            int processedBookFolders;
            int authorsImported;
            int filesImported;

            lock (_progressCacheLock)
            {
                _lastProgressByCommand.TryGetValue(key, out var last);

                // Prefer stable denominators: if TotalBookFolders isn't set, fall back to BookUnitsDiscovered.
                totalAuthorFolders = message.TotalAuthorFolders;
                totalBookFolders = message.TotalBookFolders;
                if (totalBookFolders == 0 && message.BookUnitsDiscovered > 0)
                {
                    totalBookFolders = message.BookUnitsDiscovered;
                }

                matchedAuthors = message.MatchedAuthors;
                matchedBooks = message.MatchedBooks;
                unmatchedAuthors = message.UnmatchedAuthors;
                unmatchedBooks = message.UnmatchedBooks;
                processedAuthorFolders = message.ProcessedAuthorFolders;
                processedBookFolders = message.ProcessedBookFolders;
                authorsImported = message.AuthorsImported;
                filesImported = message.FilesImported;
                overallProgressPercentage = message.OverallProgressPercentage;

                if (last != null)
                {
                    totalAuthorFolders = System.Math.Max(totalAuthorFolders, last.TotalAuthorFolders);
                    totalBookFolders = System.Math.Max(totalBookFolders, last.TotalBookFolders);
                    matchedAuthors = System.Math.Max(matchedAuthors, last.MatchedAuthors);
                    matchedBooks = System.Math.Max(matchedBooks, last.MatchedBooks);
                    unmatchedAuthors = System.Math.Max(unmatchedAuthors, last.UnmatchedAuthors);
                    unmatchedBooks = System.Math.Max(unmatchedBooks, last.UnmatchedBooks);
                    processedAuthorFolders = System.Math.Max(processedAuthorFolders, last.ProcessedAuthorFolders);
                    processedBookFolders = System.Math.Max(processedBookFolders, last.ProcessedBookFolders);
                    authorsImported = System.Math.Max(authorsImported, last.AuthorsImported);
                    filesImported = System.Math.Max(filesImported, last.FilesImported);
                    overallProgressPercentage = System.Math.Max(overallProgressPercentage, last.OverallProgressPercentage);
                }

                snapshot = new ProgressSnapshot
                {
                    Stage = message.Stage.ToString(),
                    CurrentProgress = message.CurrentProgress,
                    TotalProgress = message.TotalProgress,
                    OverallProgressPercentage = overallProgressPercentage,
                    TotalAuthorFolders = totalAuthorFolders,
                    TotalBookFolders = totalBookFolders,
                    MatchedAuthors = matchedAuthors,
                    MatchedBooks = matchedBooks,
                    UnmatchedAuthors = unmatchedAuthors,
                    UnmatchedBooks = unmatchedBooks,
                    ProcessedAuthorFolders = processedAuthorFolders,
                    ProcessedBookFolders = processedBookFolders,
                    AuthorsImported = authorsImported,
                    FilesImported = filesImported,
                    CurrentItemName = currentAuthor,
                    CurrentItemType = currentItemType,
                    CurrentBookMatched = message.CurrentBookMatched,
                    CurrentBookType = message.CurrentBookType
                };

                if (last != null && last.Equals(snapshot))
                {
                    // No material change: skip broadcast
                    return;
                }

                _lastProgressByCommand[key] = snapshot;

                // Clear cache on completion to avoid stale growth
                if (message.Stage == ImportStage.ImportComplete)
                {
                    _lastProgressByCommand.Remove(key);
                }
            }

            // Broadcast using consistent camelCase property names
            _signalRBroadcaster.BroadcastMessage(new SignalRMessage
            {
                Name = "ImportStageProgress",
                Body = new
                {
                    stage = message.Stage.ToString(),
                    message = message.Message,
                    currentProgress = message.CurrentProgress,
                    totalProgress = message.TotalProgress,
                    overallProgressPercentage = overallProgressPercentage,
                    totalAuthorFolders = totalAuthorFolders,
                    totalBookFolders = totalBookFolders,
                    matchedAuthors = matchedAuthors,
                    matchedBooks = matchedBooks,
                    unmatchedAuthors = unmatchedAuthors,
                    unmatchedBooks = unmatchedBooks,
                    processedAuthorFolders = processedAuthorFolders,
                    processedBookFolders = processedBookFolders,
                    authorsImported = authorsImported,
                    filesImported = filesImported,
                    currentItemName = currentAuthor,
                    currentItemType = currentItemType,
                    currentBookMatched = message.CurrentBookMatched,
                    currentBookType = message.CurrentBookType,
                    // Command tracking for pause/resume
                    commandId = message.CommandId,
                    commandStatus = message.CommandStatus,
                    matchedAuthorId = message.MatchedAuthorId,
                    matchedEditionId = message.MatchedEditionId
                }
            });
        }
    }
}
