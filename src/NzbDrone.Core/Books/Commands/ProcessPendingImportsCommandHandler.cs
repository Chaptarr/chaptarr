using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books.Commands
{
    public class ProcessPendingImportsCommandHandler : IExecute<ProcessPendingImportsCommand>
    {
        private readonly IPendingAuthorImportService _pendingImportService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public ProcessPendingImportsCommandHandler(
            IPendingAuthorImportService pendingImportService,
            IAuthorLibraryService authorLibraryService,
            IAuthorService authorService,
            IBookService bookService,
            IProvideBookInfo bookInfo,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _pendingImportService = pendingImportService;
            _authorLibraryService = authorLibraryService;
            _authorService = authorService;
            _bookService = bookService;
            _bookInfo = bookInfo;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Execute(ProcessPendingImportsCommand message)
        {
            try
            {
                var batchSize = GetEffectiveBatchSize(message);
                var dueItems = _pendingImportService.GetDueForProcessing(batchSize);

                if (!dueItems.Any())
                {
                    _logger.Trace("No pending author imports due for processing");
                    return;
                }

                _logger.Info("Processing {0} pending author imports", dueItems.Count);

                var attempted = 0;
                var succeeded = 0;
                var failed = 0;
                var retried = 0;
                var seenIds = new ConcurrentDictionary<int, bool>();
                var parallelism = GetEffectiveParallelism(message);

                Parallel.ForEach(dueItems,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                    item =>
                    {
                        try
                        {
                            if (!seenIds.TryAdd(item.Id, true))
                            {
                                _logger.Warn("Item {0} appeared again in same batch - possible loop", item.Id);
                            }

                            _logger.Debug("Processing pending import {0} for provider {1}", item.Id, item.ProviderId);
                            Interlocked.Increment(ref attempted);

                            var processedItem = ProcessPendingImportAsync(item).GetAwaiter().GetResult();

                            switch (processedItem.OverallStatus)
                            {
                                case PendingImportStatus.Succeeded:
                                    Interlocked.Increment(ref succeeded);
                                    break;
                                case PendingImportStatus.Failed:
                                    Interlocked.Increment(ref failed);
                                    break;
                                case PendingImportStatus.Retrying:
                                    Interlocked.Increment(ref retried);
                                    break;
                            }

                            PublishPendingImportProgress(processedItem, dueItems.Count, succeeded, failed, retried);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error processing pending import {0}", item.Id);
                            Interlocked.Increment(ref failed);
                            PublishPendingImportProgress(item, dueItems.Count, succeeded, failed, retried);
                        }
                    });

                var hasMoreDueItems = message?.ContinueUntilEmpty == true && _pendingImportService.GetDueForProcessing(1).Any();

                // Cleanup old completed items once per scheduled command or import-list drain.
                if (message?.ContinueUntilEmpty != true || message.Continuation == 0)
                {
                    _pendingImportService.CleanupOldCompleted();
                }

                if (hasMoreDueItems)
                {
                    _logger.Info("Pending import drain has more due items; queueing continuation batch {0}", message.Continuation + 1);
                    _commandQueueManager.Push(new ProcessPendingImportsCommand
                    {
                        BatchSize = batchSize,
                        ContinueUntilEmpty = true,
                        Continuation = message.Continuation + 1
                    }, CommandPriority.Normal);
                }
                else
                {
                    // Emit final ImportComplete summary for UI only when the drain is actually complete.
                    try
                    {
                        var doneEvt = new MediaFiles.Events.ImportStageProgressEvent(
                            MediaFiles.Events.ImportStage.ImportComplete,
                            $"Import complete: {succeeded} imported, {failed} failed, {retried} retrying",
                            currentProgress: succeeded,
                            totalProgress: attempted)
                        {
                            AuthorsImported = succeeded,
                            AuthorsFailed = failed,
                            AuthorsRetrying = retried
                        };
                        doneEvt.CommandId = NzbDrone.Core.ProgressMessaging.ProgressMessageContext.CommandModel?.Id;
                        _eventAggregator.PublishEvent(doneEvt);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[PROGRESS] Failed to publish final ImportComplete summary");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in ProcessPendingImportsCommand");
            }
        }


        private static int GetEffectiveBatchSize(ProcessPendingImportsCommand message)
        {
            const int DefaultBatchSize = 10;
            const int MaxBatchSize = 50;

            if (message?.BatchSize <= 0)
            {
                return DefaultBatchSize;
            }

            return Math.Min(message.BatchSize, MaxBatchSize);
        }

        private static int GetEffectiveParallelism(ProcessPendingImportsCommand message)
        {
            return message?.ContinueUntilEmpty == true ? 3 : 1;
        }

        private void PublishPendingImportProgress(PendingAuthorImport item, int total, int succeeded, int failed, int retried)
        {
            var evt = new ImportStageProgressEvent(
                ImportStage.ImportingAuthorsToDatabase,
                $"Imported {succeeded} of {total} ({failed} failed, {retried} retrying)",
                currentProgress: succeeded,
                totalProgress: total)
            {
                AuthorsImported = succeeded,
                AuthorsFailed = failed,
                AuthorsRetrying = retried,
                CurrentItemName = item.AuthorName ?? item.ProviderId,
                CurrentItemType = "author"
            };
            evt.CommandId = NzbDrone.Core.ProgressMessaging.ProgressMessageContext.CommandModel?.Id;
            _eventAggregator.PublishEvent(evt);
        }

        private async Task<PendingAuthorImport> ProcessPendingImportAsync(PendingAuthorImport item)
        {
            _logger.Info("Processing pending import {0} for provider {1}", item.Id, item.ProviderId);

            try
            {
                _logger.Info("Processing queued author import for provider {0}", item.ProviderId);

                // Fire importing notification event. AddAuthorAsync performs the metadata fetch; avoid
                // fetching the same large author payload twice just to populate this progress label.
                _eventAggregator.PublishEvent(new ImportStageProgressEvent(
                    ImportStage.ImportingAuthorsToDatabase,
                    $"Importing: {item.AuthorName ?? item.ProviderId}",
                    1, 1)
                {
                    CurrentItemName = item.AuthorName ?? item.ProviderId,
                    CurrentItemType = "author"
                });

                // Build monitoring config from pending item
                var config = BuildMonitoringConfig(item);

                // Don't re-queue if we're already processing
                config.QueueIfUnavailable = false;

                // Import the author - but with a flag to prevent re-queuing on failure
                config.IsFromQueue = true;

                var addedAuthor = await _authorLibraryService.AddAuthorAsync(item.ProviderId, config);

                if (addedAuthor != null && addedAuthor.Id > 0)
                {
                    var requestedWorks = GetRequestedWorks(item);
                    ResolveLocalBooks(requestedWorks, addedAuthor);

                    if (requestedWorks.Any(target => target.Book == null))
                    {
                        var notReady = new List<string>();
                        foreach (var target in requestedWorks.Where(target => target.Book == null))
                        {
                            // This lookup is only a rescue signal. The returned work is never inserted;
                            // the refreshed author catalog remains the sole source of local Book rows.
                            try
                            {
                                _bookInfo.GetWorkInfo(target.ProviderId, target.MediaType, item.ProviderId);
                            }
                            catch (BookNotFoundException)
                            {
                                notReady.Add(target.ProviderId);
                            }
                        }

                        if (notReady.Any())
                        {
                            var reason = $"Requested work(s) {string.Join(", ", notReady)} are still being prepared by the metadata server.";
                            _logger.Debug("Pending book request for author {0} is not ready; scheduling retry", item.ProviderId);
                            _pendingImportService.ScheduleRetry(item, reason);
                            return item;
                        }

                        addedAuthor = await _authorLibraryService.AddAuthorAsync(item.ProviderId, config);
                        ResolveLocalBooks(requestedWorks, addedAuthor);
                    }

                    var missingTargets = requestedWorks
                        .Where(target => target.Book == null)
                        .Select(target => $"{target.MediaType} work {target.ProviderId}")
                        .ToList();
                    if (missingTargets.Any())
                    {
                        var reason = $"Requested {string.Join(", ", missingTargets)} is not yet present in the authoritative author catalog.";
                        _logger.Debug("Pending author import {0} retained: {1}", item.ProviderId, reason);
                        _pendingImportService.ScheduleRetry(item, reason);
                        return item;
                    }

                    addedAuthor = ApplyRequestedMonitoring(item, addedAuthor, requestedWorks);
                    ResolveLocalBooks(requestedWorks, addedAuthor);
                    var requestedSearchBookIds = GetRequestedSearchBookIds(addedAuthor, requestedWorks);
                    QueueRequestedSearches(item, addedAuthor, requestedSearchBookIds);

                    _logger.Info("Successfully imported author {0} (ID: {1})", item.ProviderId, addedAuthor.Id);
                    MarkSucceeded(item);
                    if (!_pendingImportService.TryDeleteIfUnchanged(item))
                    {
                        // Another request was merged while this worker was running. Leave the row
                        // active so the newly added target is processed rather than deleted as stale.
                        _logger.Debug("Pending import {0} changed while it was processing; retaining the merged request", item.Id);
                        return _pendingImportService.GetByProviderId(item.ProviderId) ?? item;
                    }

                    _eventAggregator.PublishEvent(new PendingAuthorImportSucceededEvent(item, addedAuthor));
                }
                else
                {
                    throw new Exception("Failed to add author - unknown error");
                }
            }
            catch (AuthorTerminalException ex)
            {
                var declaredReason = ex.Message;
                _logger.Warn("Pending author import {0} failed with declared terminal {1}; automatic retry stopped. Reopenable: {2}",
                    item.ProviderId, ex.Code, ex.Reopenable);
                _pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, declaredReason);
                _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
            }
            catch (WorkRescueTerminalException ex)
            {
                _logger.Warn("Pending book request {0} reached a declared terminal rescue state: {1}", ex.ProviderId, ex.Message);
                _pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, ex.Message);
                _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
            }

            catch (AuthorNotFoundException)
            {
                // Author still not available, schedule retry
                _logger.Debug("Author {0} not yet available on metadata server, scheduling retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
            }
            catch (BookNotFoundException ex)
            {
                var reason = $"Requested book {ex.Message} is still being prepared by the metadata server.";
                _logger.Debug("Pending book request for author {0} is not ready; scheduling retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, reason);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing pending import {0}", item.Id);

                if (IsPermanentError(ex))
                {
                    _pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, ex.Message);
                    _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
                }
                else
                {
                    _pendingImportService.ScheduleRetry(item, ex.Message);
                }
            }

            return item;
        }

        private MonitoringConfig BuildMonitoringConfig(PendingAuthorImport item)
        {
            var audiobookMonitorTargets = DeserializeProviderIds(item.AudiobookBooksToMonitor, nameof(item.AudiobookBooksToMonitor));
            var audiobookSearchTargets = DeserializeProviderIds(item.AudiobookBooksToSearch, nameof(item.AudiobookBooksToSearch));
            var ebookMonitorTargets = DeserializeProviderIds(item.EbookBooksToMonitor, nameof(item.EbookBooksToMonitor));
            var ebookSearchTargets = DeserializeProviderIds(item.EbookBooksToSearch, nameof(item.EbookBooksToSearch));
            var config = new MonitoringConfig
            {
                AudiobookMonitored = item.AudiobookMonitored,
                AudiobookMonitorNewItems = item.AudiobookMonitorNewItems,
                AudiobookMonitorExistingMode = item.AudiobookMonitorExistingMode,
                EbookMonitored = item.EbookMonitored,
                EbookMonitorNewItems = item.EbookMonitorNewItems,
                EbookMonitorExistingMode = item.EbookMonitorExistingMode,
                AudiobookQualityProfileId = item.AudiobookQualityProfileId,
                EbookQualityProfileId = item.EbookQualityProfileId,
                AudiobookMetadataProfileId = item.AudiobookMetadataProfileId,
                EbookMetadataProfileId = item.EbookMetadataProfileId,
                AudiobookRootFolderPath = item.AudiobookRootFolderPath,
                EbookRootFolderPath = item.EbookRootFolderPath,
                DiscoveredAuthorFolderPath = item.DiscoveredAuthorFolderPath,
                SearchForMissingBooks = item.SearchForMissingBooks,
                CreateAudiobook = item.HasAudiobook(),
                CreateEbook = item.HasEbook(),
                AudiobookBooksToMonitor = audiobookMonitorTargets,
                AudiobookBooksToSearch = audiobookSearchTargets,
                EbookBooksToMonitor = ebookMonitorTargets,
                EbookBooksToSearch = ebookSearchTargets
            };

            // Deserialize tags if present
            if (!string.IsNullOrEmpty(item.Tags))
            {
                config.Tags = JsonConvert.DeserializeObject<HashSet<int>>(item.Tags);
            }

            return config;
        }

        private Author ApplyRequestedMonitoring(PendingAuthorImport item, Author author, List<RequestedWork> requestedWorks)
        {
            var audiobookTargets = requestedWorks.Where(target => target.MediaType == BookMediaType.Audiobook).ToList();
            var ebookTargets = requestedWorks.Where(target => target.MediaType == BookMediaType.Ebook).ToList();

            if (!audiobookTargets.Any() && !ebookTargets.Any())
            {
                return author;
            }

            if (item.HasAudiobook() && audiobookTargets.Any())
            {
                _authorService.EnsureMediaTypeMonitoring(author.Id, "audiobook");
            }

            if (item.HasEbook() && ebookTargets.Any())
            {
                _authorService.EnsureMediaTypeMonitoring(author.Id, "ebook");
            }

            author = _authorService.GetAuthor(author.Id) ?? author;
            ApplyRequestedBookMonitoring(BookMediaType.Audiobook, item.HasAudiobook(), audiobookTargets);
            ApplyRequestedBookMonitoring(BookMediaType.Ebook, item.HasEbook(), ebookTargets);

            return _authorService.GetAuthor(author.Id) ?? author;
        }

        private static List<string> MergeProviderIds(params List<string>[] providerIdLists)
        {
            return providerIdLists
                .Where(list => list != null)
                .SelectMany(list => list)
                .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ApplyRequestedBookMonitoring(
            BookMediaType mediaType,
            bool mediaTypeRequested,
            List<RequestedWork> requestedWorks)
        {
            if (!mediaTypeRequested || !requestedWorks.Any())
            {
                return;
            }

            var ids = requestedWorks
                .Select(target => target.Book?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (ids.Any())
            {
                _bookService.SetMonitoredForMediaType(
                    ids,
                    mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                    true);
            }
        }

        private static HashSet<int> GetRequestedSearchBookIds(Author author, List<RequestedWork> requestedWorks)
        {
            return requestedWorks
                .Where(target => target.Search)
                .Select(target => target.Book)
                .Where(book => book != null)
                .Select(book =>
                {
                    book.Author = author;
                    if (!book.IsMonitoredWithAuthor())
                    {
                        throw new InvalidOperationException($"Requested {book.MediaType} book {book.Id} remained unmonitored after applying the saved add settings.");
                    }

                    return book.Id;
                })
                .ToHashSet();
        }

        private void QueueRequestedSearches(PendingAuthorImport item, Author author, HashSet<int> bookIds)
        {
            if (item.SearchForMissingBooks)
            {
                _commandQueueManager.Push(new MissingBookSearchCommand
                {
                    AuthorId = author.Id
                });
                _logger.Debug("Queued missing book search for imported author {0} (ID: {1})", item.ProviderId, author.Id);
            }

            if (bookIds.Any())
            {
                _commandQueueManager.Push(new BookSearchCommand(bookIds.ToList()));
                _logger.Debug("Queued exact search for {0} requested books after importing author {1} (ID: {2})", bookIds.Count, item.ProviderId, author.Id);
            }
        }

        private List<RequestedWork> GetRequestedWorks(PendingAuthorImport item)
        {
            var audiobookSearch = DeserializeProviderIds(item.AudiobookBooksToSearch, nameof(item.AudiobookBooksToSearch)) ?? new List<string>();
            var ebookSearch = DeserializeProviderIds(item.EbookBooksToSearch, nameof(item.EbookBooksToSearch)) ?? new List<string>();
            var requested = new List<RequestedWork>();

            AddRequestedWorks(requested, BookMediaType.Audiobook,
                MergeProviderIds(DeserializeProviderIds(item.AudiobookBooksToMonitor, nameof(item.AudiobookBooksToMonitor)), audiobookSearch),
                audiobookSearch);
            AddRequestedWorks(requested, BookMediaType.Ebook,
                MergeProviderIds(DeserializeProviderIds(item.EbookBooksToMonitor, nameof(item.EbookBooksToMonitor)), ebookSearch),
                ebookSearch);

            return requested;
        }

        private static void AddRequestedWorks(List<RequestedWork> requested, BookMediaType mediaType, List<string> providerIds, List<string> searchIds)
        {
            foreach (var providerId in providerIds ?? new List<string>())
            {
                requested.Add(new RequestedWork
                {
                    ProviderId = providerId,
                    MediaType = mediaType,
                    Search = searchIds.Contains(providerId, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        private void ResolveLocalBooks(List<RequestedWork> requestedWorks, Author author)
        {
            foreach (var target in requestedWorks)
            {
                var normalized = NormalizeProviderId(target.ProviderId);
                var separator = normalized?.IndexOf(':') ?? -1;
                if (separator <= 0)
                {
                    throw new InvalidOperationException($"Invalid requested work provider ID '{target.ProviderId}'.");
                }

                var matches = _bookService.FindAllByWorkProviderId(
                        normalized.Substring(0, separator),
                        ProviderIdHelper.StripPrefix(normalized),
                        target.MediaType)
                    .Where(book => book.AuthorId == author.Id)
                    .GroupBy(book => book.Id)
                    .Select(group => group.First())
                    .ToList();

                if (matches.Count > 1)
                {
                    throw new InvalidOperationException($"Requested work '{target.ProviderId}' resolves to multiple local {target.MediaType} books.");
                }

                target.Book = matches.SingleOrDefault();
            }
        }

        private static void MarkSucceeded(PendingAuthorImport item)
        {
            item.AudiobookStatus = item.HasAudiobook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
            item.EbookStatus = item.HasEbook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
            item.OverallStatus = PendingImportStatus.Succeeded;
            item.LastError = null;
            item.LastAttemptAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
        }

        private sealed class RequestedWork
        {
            public string ProviderId { get; init; }
            public BookMediaType MediaType { get; init; }
            public bool Search { get; init; }
            public Book Book { get; set; }
        }

        private List<string> DeserializeProviderIds(string json, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<List<string>>(json);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to deserialize {0}: {1}", fieldName, json);
                return null;
            }
        }

        private bool IsPermanentError(Exception ex)
        {
            return ex.Message.Contains("Quality profile", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Metadata profile", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Root folder", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("remained unmonitored", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return providerId;

            var colonIndex = providerId.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = providerId.Substring(0, colonIndex).ToLowerInvariant();
                var id = providerId.Substring(colonIndex + 1).Trim();
                return $"{prefix}:{id}";
            }

            return providerId.Trim();
        }
    }
}
