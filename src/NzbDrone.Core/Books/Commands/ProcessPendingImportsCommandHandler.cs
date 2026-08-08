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
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public ProcessPendingImportsCommandHandler(
            IPendingAuthorImportService pendingImportService,
            IAuthorLibraryService authorLibraryService,
            IAuthorService authorService,
            IBookService bookService,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _pendingImportService = pendingImportService;
            _authorLibraryService = authorLibraryService;
            _authorService = authorService;
            _bookService = bookService;
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
                // Check if author was added manually while pending
                var providerPrefix = ExtractPrefix(item.ProviderId);
                var providerIdWithoutPrefix = ExtractIdWithoutPrefix(item.ProviderId);
                var existingAuthor = _authorService.FindByProviderId(providerPrefix, providerIdWithoutPrefix);
                if (existingAuthor != null)
                {
                    _logger.Info("Author {0} already exists in library, marking as succeeded", item.ProviderId);

                    if (item.SearchForMissingBooks)
                    {
                        _commandQueueManager.Push(new MissingBookSearchCommand
                        {
                            AuthorId = existingAuthor.Id
                        });
                        _logger.Debug("Queued missing book search for existing author {0} (ID: {1})", item.ProviderId, existingAuthor.Id);
                    }

                    _pendingImportService.UpdateStatus(item, PendingImportStatus.Succeeded, null);
                    // Delete row on success to prevent reprocessing loops
                    _pendingImportService.Delete(item.Id);
                    return item;
                }

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

                // Handle book-specific monitoring if needed
                if (addedAuthor != null && addedAuthor.Id > 0)
                {
                    await HandleBookSpecificMonitoring(item, addedAuthor);

                    if (item.SearchForMissingBooks)
                    {
                        _commandQueueManager.Push(new MissingBookSearchCommand
                        {
                            AuthorId = addedAuthor.Id
                        });
                        _logger.Debug("Queued missing book search for newly imported author {0} (ID: {1})", item.ProviderId, addedAuthor.Id);
                    }

                    _logger.Info("Successfully imported author {0} (ID: {1})", item.ProviderId, addedAuthor.Id);
                    _pendingImportService.UpdateStatus(item, PendingImportStatus.Succeeded, null);
                    // Delete row on success to prevent reprocessing loops
                    _pendingImportService.Delete(item.Id);
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

            catch (AuthorNotFoundException)
            {
                // Author still not available, schedule retry
                _logger.Debug("Author {0} not yet available on metadata server, scheduling retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
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
            var config = new MonitoringConfig
            {
                AudiobookMonitorExisting = item.AudiobookMonitorExisting,
                AudiobookMonitorFuture = item.AudiobookMonitorFuture,
                EbookMonitorExisting = item.EbookMonitorExisting,
                EbookMonitorFuture = item.EbookMonitorFuture,
                MonitorExisting = (item.AudiobookMonitorExisting ?? item.EbookMonitorExisting ?? 0) > 0,
                MonitorFuture = item.AudiobookMonitorFuture ?? item.EbookMonitorFuture ?? true,
                AudiobookQualityProfileId = item.AudiobookQualityProfileId,
                EbookQualityProfileId = item.EbookQualityProfileId,
                AudiobookMetadataProfileId = item.AudiobookMetadataProfileId,
                EbookMetadataProfileId = item.EbookMetadataProfileId,
                AudiobookRootFolderPath = item.AudiobookRootFolderPath,
                EbookRootFolderPath = item.EbookRootFolderPath,
                DiscoveredAuthorFolderPath = item.DiscoveredAuthorFolderPath,
                SearchForMissingBooks = item.SearchForMissingBooks,
                CreateAudiobook = item.HasAudiobook(),
                CreateEbook = item.HasEbook()
            };

            // Deserialize tags if present
            if (!string.IsNullOrEmpty(item.Tags))
            {
                config.Tags = JsonConvert.DeserializeObject<HashSet<int>>(item.Tags);
            }

            return config;
        }

        private async Task HandleBookSpecificMonitoring(PendingAuthorImport item, Author addedAuthor)
        {
            var audiobookBooksToMonitor = DeserializeBooksToMonitor(item.AudiobookBooksToMonitor);
            var ebookBooksToMonitor = DeserializeBooksToMonitor(item.EbookBooksToMonitor);

            if (audiobookBooksToMonitor?.Any() == true && item.HasAudiobook())
            {
                await UnmonitorAllExceptSpecified(addedAuthor, audiobookBooksToMonitor,
                    processAudiobooks: true, processEbooks: false);
            }

            if (ebookBooksToMonitor?.Any() == true && item.HasEbook())
            {
                await UnmonitorAllExceptSpecified(addedAuthor, ebookBooksToMonitor,
                    processAudiobooks: false, processEbooks: true);
            }
        }

        private Task UnmonitorAllExceptSpecified(Author author, List<string> booksToMonitor,
            bool processAudiobooks, bool processEbooks)
        {
            var allBooks = _bookService.GetBooksByAuthor(author.Id);
            var booksToUpdate = new List<Book>();

            foreach (var book in allBooks)
            {
                var shouldMonitor = booksToMonitor.Any(btm =>
                    BookMatchesProviderId(book, btm));

                if (book.MediaType == BookMediaType.Audiobook && processAudiobooks)
                {
                    book.AudiobookMonitored = shouldMonitor;
                    booksToUpdate.Add(book);
                }
                else if (book.MediaType == BookMediaType.Ebook && processEbooks)
                {
                    book.EbookMonitored = shouldMonitor;
                    booksToUpdate.Add(book);
                }
            }

            if (booksToUpdate.Any())
            {
                _bookService.UpdateMany(booksToUpdate);
                _logger.Info("Updated monitoring for {0} books - monitoring only specified books", booksToUpdate.Count);
            }

            return Task.CompletedTask;
        }

        private bool BookMatchesProviderId(Book book, string providerId)
        {
            providerId = NormalizeProviderId(providerId);

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(providerId))
                return true;

            if (!string.IsNullOrEmpty(book.HardcoverBookId) &&
                NormalizeProviderId(ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc")) == providerId)
                return true;

            if (!string.IsNullOrEmpty(book.GoodreadsWorkId) &&
                NormalizeProviderId(ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr")) == providerId)
                return true;

            if (!string.IsNullOrEmpty(book.OpenLibraryWorkId) &&
                NormalizeProviderId(ProviderIdHelper.Canonicalize(book.OpenLibraryWorkId, "ol")) == providerId)
                return true;

            if (BookEditionIdentity.GetGoogleBooksEditionId(book) is string googleBooksEditionId &&
                NormalizeProviderId(ProviderIdHelper.Canonicalize(googleBooksEditionId, "gb")) == providerId)
                return true;

            if (BookEditionIdentity.GetAsin(book) is string asin &&
                NormalizeProviderId(asin.Contains(":")
                    ? ProviderIdHelper.Normalize(asin, "az")
                    : ProviderIdHelper.WithPrefix("az", asin)) == providerId)
                return true;

            return false;
        }

        private List<string> DeserializeBooksToMonitor(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<List<string>>(json);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to deserialize BooksToMonitor: {0}", json);
                return null;
            }
        }

        private bool IsPermanentError(Exception ex)
        {
            return ex.Message.Contains("Quality profile", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Metadata profile", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Root folder", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase);
        }

        private string ExtractPrefix(string providerId)
        {
            var colonIndex = providerId?.IndexOf(':') ?? -1;
            return colonIndex > 0 ? providerId.Substring(0, colonIndex).ToLowerInvariant() : null;
        }

        private string ExtractIdWithoutPrefix(string providerId)
        {
            var colonIndex = providerId?.IndexOf(':') ?? -1;
            return colonIndex > 0 ? providerId.Substring(colonIndex + 1) : providerId;
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
