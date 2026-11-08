using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaCover.Commands;
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
        private readonly IBookService _bookService;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IProviderAliasService _providerAliases;

        public ProcessPendingImportsCommandHandler(
            IPendingAuthorImportService pendingImportService,
            IAuthorLibraryService authorLibraryService,
            IBookService bookService,
            IProvideBookInfo bookInfo,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            Logger logger,
            IProviderAliasService providerAliases = null)
        {
            _pendingImportService = pendingImportService;
            _authorLibraryService = authorLibraryService;
            _bookService = bookService;
            _bookInfo = bookInfo;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _providerAliases = providerAliases;
        }

        public void Execute(ProcessPendingImportsCommand message)
        {
            try
            {
                var batchSize = GetEffectiveBatchSize(message);
                var dueItems = _pendingImportService.GetDueForProcessing(batchSize);

                // Retain completed history on the existing schedule even when no work is due.
                if (message?.ContinueUntilEmpty != true || message.Continuation == 0)
                {
                    try { _pendingImportService.CleanupOldCompleted(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger.Warn(ex, "Unable to clean completed pending imports; continuing the due batch"); }
                }

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
                                case PendingImportStatus.PartialSuccess:
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
            var attemptTimer = Stopwatch.StartNew();
            var remoteWorkLookups = 0;
            var handedOffTargets = 0;
            var progress = SnapshotProgress(item);
            void SaveProgress()
            {
                if (!_pendingImportService.SaveProgress(item, progress)) throw new OperationCanceledException("Pending request became inactive.");
                progress = SnapshotProgress(item);
            }
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

                var requestedAudiobook = config.CreateAudiobook;
                var requestedEbook = config.CreateEbook;
                var requestedWorks = GetRequestedWorks(config);
                var requestedCounts = requestedWorks.GroupBy(target => target.MediaType).ToDictionary(group => group.Key, group => group.Count());
                var independentAuthorDemand = HasIndependentAuthorDemand(config, requestedWorks);
                Author addedAuthor = null;
                Exception originalAuthorFailure = null;
                try
                {
                    addedAuthor = await _authorLibraryService.AddAuthorAsync(item.ProviderId, config);
                    if (addedAuthor == null || addedAuthor.Id <= 0)
                        throw new InvalidOperationException("Failed to add author - unknown error");
                    if (!string.IsNullOrWhiteSpace(addedAuthor.Name) && AuthorIdentity.GetProviderIdentityTokens(addedAuthor).Contains(item.ProviderId))
                        item.AuthorName = addedAuthor.Name;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AuthorIdentityUnverifiedException)
                {
                    throw;
                }
                catch (Exception ex) when (requestedWorks.Count > 0)
                {
                    originalAuthorFailure = ex;
                    addedAuthor = null;
                    _logger.Warn(ex, "Original author {0} is unavailable; resolving its requested works independently", item.ProviderId);
                }
                if (requestedWorks.Any(target => target.Narration != null))
                    item.AudiobookNarrations = JsonConvert.SerializeObject(requestedWorks.Where(target => target.Narration != null).Select(target => target.Narration));

                var excludedAudiobook = originalAuthorFailure == null && requestedAudiobook && !config.CreateAudiobook;
                var excludedEbook = originalAuthorFailure == null && requestedEbook && !config.CreateEbook;
                var excludedReasons = new List<string>();
                if (excludedAudiobook) excludedReasons.Add(ExcludedAuthorReason(item.ProviderId, BookMediaType.Audiobook));
                if (excludedEbook) excludedReasons.Add(ExcludedAuthorReason(item.ProviderId, BookMediaType.Ebook));
                var catalog = addedAuthor != null ? _bookService.GetBooksByAuthor(addedAuthor.Id).ToList() : new List<Book>();
                var importedAuthors = new Dictionary<int, Author>();
                var notReady = new List<string>();
                var handoffFailures = new List<string>();
                if (originalAuthorFailure == null)
                {
                    importedAuthors[addedAuthor.Id] = addedAuthor;
                    ResolveLocalBooks(requestedWorks.Where(target => target.MediaType == BookMediaType.Audiobook
                        ? config.CreateAudiobook : config.CreateEbook).ToList(), addedAuthor, config, catalog);
                }

                if (requestedWorks.Any(target => target.Book == null && !target.NarrationUnavailable && target.Failure == null))
                {
                    var primaryTargets = new Dictionary<string, (Author Author, List<RequestedWork> Targets)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var targets in requestedWorks.Where(target => target.Book == null && !target.NarrationUnavailable && target.Failure == null)
                                 .GroupBy(target => (target.MediaType, ProviderId: ProviderIdHelper.TryNormalize(target.ProviderId, null, out var normalized)
                                     ? normalized.ToLowerInvariant() : target.ProviderId)))
                    {
                        var target = targets.First();
                        // This lookup is only a rescue signal. The returned work is never inserted;
                        // the refreshed author catalog remains the sole source of local Book rows.
                        try
                        {
                            remoteWorkLookups++;
                            var metadata = _bookInfo.GetWorkInfo(target.ProviderId, target.MediaType,
                                AuthorIdentity.NormalizeWorkLookupAuthorHint(target.ProviderId, item.ProviderId));
                            var primary = BookIdentity.GetRequestedWorkPrimaryAuthor(metadata, target.ProviderId, target.MediaType);
                            var providerId = AuthorIdentity.GetPreferredProviderId(primary);
                            if (providerId == null)
                            {
                                notReady.Add(target.ProviderId);
                                continue;
                            }
                            if (!primaryTargets.TryGetValue(providerId, out var group))
                                group = (primary, new List<RequestedWork>());
                            group.Targets.AddRange(targets);
                            primaryTargets[providerId] = group;
                        }
                        catch (Exception ex) when (ex is WorkRescueTerminalException || ex is InvalidProviderIdException)
                        {
                            foreach (var failed in targets) failed.Failure = ex.Message;
                            _logger.Warn("Requested work {0} failed: {1}", target.ProviderId, ex.Message);
                        }
                        catch (BookNotFoundException)
                        {
                            notReady.Add(target.ProviderId);
                        }
                        catch (Exception ex) when (ex is BadRequestException || ex is NzbDrone.Core.MetadataSource.BookInfo.BookInfoException)
                        {
                            notReady.Add(target.ProviderId);
                            _logger.Warn(ex, "Primary metadata for requested work {0} is unavailable", target.ProviderId);
                        }
                    }

                    foreach (var (providerId, group) in primaryTargets)
                    {
                        try
                        {
                            var originalOwner = ProviderIdHelper.TryNormalize(item.ProviderId, null, out var originalId) &&
                                AuthorIdentity.GetProviderIdentityTokens(group.Author).Contains(originalId);
                            if (originalOwner && originalAuthorFailure != null)
                            {
                                if (originalAuthorFailure is AuthorTerminalException || originalAuthorFailure is ValidationException validation &&
                                    validation.Errors.Any(error => error.ErrorCode == AuthorLibraryService.ImportListAuthorExcludedErrorCode))
                                    foreach (var target in group.Targets) target.Failure = originalAuthorFailure.Message;
                                else
                                    notReady.AddRange(group.Targets.Select(target => target.ProviderId));
                                continue;
                            }
                            if (originalOwner)
                            {
                                MarkExcludedRequests(group.Targets, config, item.ProviderId);
                                if (group.Targets.All(target => target.Failure != null)) continue;
                            }
                            var ownerConfig = originalOwner ? config : BuildRoutedBookConfig(item, group.Author, group.Targets, config);
                            if (!originalOwner)
                            {
                                // Retain source demand until the normal primary queue owns every
                                // target, so retries cannot repeat completed monitoring or searches.
                                if (!_pendingImportService.IsActiveRequest(item.Id, item.Version))
                                    throw new OperationCanceledException("Pending request became inactive or changed before primary handoff.");
                                var pendingId = await _pendingImportService.EnqueueAsync(providerId, ownerConfig, item.SourceApplication ?? item.RequestedBy);
                                if (pendingId <= 0) throw new InvalidOperationException($"Unable to queue selected primary {providerId}.");
                                _logger.Info("Handed {0} requested targets to selected primary {1} (pending import {2})", group.Targets.Count, providerId, pendingId);
                                PruneAppliedTargets(item, group.Targets, monitoring: true, searches: true);
                                requestedWorks.RemoveAll(group.Targets.Contains);
                                item.AudiobookNarrations = JsonConvert.SerializeObject(requestedWorks.Where(target => target.Narration != null).Select(target => target.Narration));
                                handedOffTargets += group.Targets.Count;
                                SaveProgress();
                                config.PendingImportVersion = item.Version;
                                continue;
                            }
                            var owner = await _authorLibraryService.AddAuthorAsync(item.ProviderId, ownerConfig);
                            if (owner == null || owner.Id <= 0) throw new InvalidOperationException($"Unable to import selected primary {providerId}.");
                            importedAuthors[owner.Id] = owner;
                            var ownerCatalog = _bookService.GetBooksByAuthor(owner.Id);
                            catalog.RemoveAll(book => book.AuthorId == owner.Id);
                            catalog.AddRange(ownerCatalog);
                            MarkExcludedRequests(group.Targets, ownerConfig, providerId);
                            ResolveLocalBooks(group.Targets.Where(target => target.Failure == null).ToList(), owner, ownerConfig, ownerCatalog);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is AuthorTerminalException || ex is WorkRescueTerminalException ||
                            ex is ValidationException validation && validation.Errors.Any(error => error.ErrorCode == AuthorLibraryService.ImportListAuthorExcludedErrorCode))
                        {
                            foreach (var target in group.Targets) target.Failure = ex.Message;
                            _logger.Warn(ex, "Selected primary {0} cannot fulfill its requested works", providerId);
                        }
                        catch (Exception ex)
                        {
                            notReady.AddRange(group.Targets.Select(target => target.ProviderId));
                            if (!ProviderIdHelper.TryNormalize(item.ProviderId, null, out var sourceId) ||
                                !AuthorIdentity.GetProviderIdentityTokens(group.Author).Contains(sourceId))
                                handoffFailures.Add($"Unable to hand requested works to selected primary {providerId}: {ex.Message}");
                            _logger.Warn(ex, "Selected primary {0} is unavailable; retaining its requested works", providerId);
                        }
                    }
                    if (requestedWorks.Any(target => target.Narration != null))
                        item.AudiobookNarrations = JsonConvert.SerializeObject(requestedWorks.Where(target => target.Narration != null).Select(target => target.Narration));
                }

                var missingTargets = requestedWorks
                    .Where(target => target.Book == null && !target.NarrationUnavailable && target.Failure == null)
                    .Select(target => $"{target.MediaType} work {target.ProviderId}")
                    .ToList();
                if (item.SearchForMissingBooks && (originalAuthorFailure != null || config.SearchForMissingBooks == false) &&
                    MonitoringConfig.IsImportListRequestOrigin(item.RequestedBy) && requestedWorks.Any())
                {
                    // Preserve exact unfinished work searches before clearing aggregate intent.
                    // Queueing decides whether a ready row is covered by a held author search.
                    foreach (var target in requestedWorks.Where(target => !target.NarrationUnavailable && target.Failure == null))
                    {
                        var implicitSearch = IsImplicitRequestedSearch(item, config, target);
                        if (implicitSearch)
                        {
                            target.Search = true;
                            if (target.Narration != null) target.Narration.Search = target.Search;
                        }
                    }

                    if (requestedAudiobook && config.AudiobookBooksToSearch == null)
                        item.AudiobookBooksToSearch = JsonConvert.SerializeObject(requestedWorks.Where(target => target.MediaType == BookMediaType.Audiobook && target.Search)
                            .Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
                    if (requestedEbook && config.EbookBooksToSearch == null)
                        item.EbookBooksToSearch = JsonConvert.SerializeObject(requestedWorks.Where(target => target.MediaType == BookMediaType.Ebook && target.Search)
                            .Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
                    if (requestedWorks.Any(target => target.Narration != null))
                        item.AudiobookNarrations = JsonConvert.SerializeObject(requestedWorks.Where(target => target.Narration != null).Select(target => target.Narration));
                    if (originalAuthorFailure == null) item.SearchForMissingBooks = false;
                }

                var searchFailures = PinRequestedNarrations(requestedWorks, config, catalog);
                ApplyRequestedMonitoring(item, requestedWorks);
                var selectedRows = _bookService.GetBooks(requestedWorks.Where(target => target.Book != null).Select(target => target.Book.Id).Distinct()).ToDictionary(book => book.Id);
                foreach (var target in requestedWorks.Where(target => target.Book != null)) target.Book = selectedRows.GetValueOrDefault(target.Book.Id);
                var withdrawnSearches = requestedWorks.Where(target => !target.Monitor && !target.AddOnly && target.Book != null &&
                    (!target.Book.IsMonitored() || !MatchesCurrentRequestedSelection(target.Book, target))).ToList();
                var remainingSearchRows = requestedWorks.Except(withdrawnSearches).Where(target => target.Search && target.Book != null)
                    .Select(target => target.Book.Id).ToHashSet();
                var heldWithdrawn = withdrawnSearches.Select(target => target.Book).DistinctBy(book => book.Id)
                    .Where(book => book.AddOptions?.SearchForNewBook == true && !remainingSearchRows.Contains(book.Id)).ToList();
                if (heldWithdrawn.Count > 0)
                {
                    if (!_pendingImportService.IsActiveRequest(item.Id, item.Version))
                        throw new OperationCanceledException("Pending request became inactive or changed before withdrawing held searches.");
                    // Rejected searches must not survive withdrawal through the post-scan row flags.
                    heldWithdrawn.ForEach(book => book.AddOptions.SearchForNewBook = false);
                    _bookService.SetAddOptions(heldWithdrawn);
                }
                PruneAppliedTargets(item, withdrawnSearches, monitoring: false, searches: true);
                requestedWorks.RemoveAll(withdrawnSearches.Contains);
                var requestedSearchBookIds = GetRequestedSearchBookIds(addedAuthor, requestedWorks);
                PruneAppliedTargets(item, requestedWorks.Where(target => target.Book != null && target.Failure == null && !target.NarrationUnavailable).ToList(), monitoring: true);
                SaveProgress();
                searchFailures.AddRange(QueueRequestedSearches(item, addedAuthor, requestedWorks.Where(target => requestedSearchBookIds.Contains(target.Book?.Id ?? 0))
                    .Select(target => target.Book).DistinctBy(book => book.Id).ToList(), requestedWorks, SaveProgress));
                PruneAppliedTargets(item, requestedWorks.Where(target => target.Book != null && target.Failure == null && !target.NarrationUnavailable && !target.Search).ToList(), monitoring: false);
                SaveProgress();
                foreach (var owner in importedAuthors.Values.Where(owner => owner.AddOptions != null))
                {
                    try
                    {
                        _commandQueueManager.Push(new RefreshAuthorCommand(owner.Id, refreshMetadata: false, rescanFolders: true, isNewAuthor: true));
                    }
                    catch (Exception ex)
                    {
                        searchFailures.Add(ex);
                        _logger.Warn(ex, "Post-import scan for author {0} was rejected", owner.Id);
                    }
                }

                if (searchFailures.Count > 0)
                {
                    var reason = string.Join("; ", searchFailures.Select(error => error.Message));
                    if (originalAuthorFailure != null) reason += $" Original author: {originalAuthorFailure.Message}";
                    _pendingImportService.ScheduleRetry(item, reason);
                    return item;
                }
                if (missingTargets.Any())
                {
                    var reason = handoffFailures.Any() ? string.Join("; ", handoffFailures) : notReady.Any()
                        ? $"Requested work(s) {string.Join(", ", notReady)} are still being prepared by the metadata server."
                        : $"Requested {string.Join(", ", missingTargets)} is not yet present in the authoritative author catalog.";
                    if (originalAuthorFailure != null) reason += $" Original author: {originalAuthorFailure.Message}";
                    _logger.Warn("Pending author import {0} retained: {1}", item.ProviderId, reason);
                    if (originalAuthorFailure is AuthorTerminalException)
                        _logger.Warn("Original author {0} has a declared terminal outcome; its requested works still require preparation", item.ProviderId);
                    _pendingImportService.ScheduleRetry(item, reason);
                    return item;
                }
                if (originalAuthorFailure != null && independentAuthorDemand)
                {
                    _logger.Warn("Requested works for original author {0}: {1} resolved; preserving its original author outcome",
                        item.ProviderId, requestedWorks.Count(target => target.Book != null && target.Failure == null));
                    ExceptionDispatchInfo.Capture(originalAuthorFailure).Throw();
                }
                var unavailable = requestedWorks.Where(target => target.NarrationUnavailable || target.Failure != null).ToList();
                if (unavailable.Count > 0 || excludedReasons.Count > 0)
                {
                    var reason = string.Join(" ", unavailable.Select(target => target.Failure ?? UnavailableEditionReason(target)).Concat(excludedReasons).Distinct());
                    item.AudiobookStatus = !requestedAudiobook ? PendingImportStatus.NotRequested :
                        excludedAudiobook ? PendingImportStatus.Failed :
                        unavailable.All(target => target.MediaType == BookMediaType.Ebook) ? PendingImportStatus.Succeeded :
                        unavailable.Count(target => target.MediaType == BookMediaType.Audiobook) == requestedCounts.GetValueOrDefault(BookMediaType.Audiobook)
                        ? PendingImportStatus.Failed : PendingImportStatus.PartialSuccess;
                    item.EbookStatus = !requestedEbook ? PendingImportStatus.NotRequested :
                        excludedEbook ? PendingImportStatus.Failed :
                        unavailable.All(target => target.MediaType == BookMediaType.Audiobook) ? PendingImportStatus.Succeeded :
                        unavailable.Count(target => target.MediaType == BookMediaType.Ebook) == requestedCounts.GetValueOrDefault(BookMediaType.Ebook)
                        ? PendingImportStatus.Failed : PendingImportStatus.PartialSuccess;
                    item.UpdateOverallStatus();
                    var status = item.OverallStatus;
                    if (_pendingImportService.UpdateStatus(item, status, reason, item.Version))
                    {
                        _logger.Warn("Pending import {0}: {1}", item.ProviderId, reason);
                        if (status == PendingImportStatus.PartialSuccess)
                            _eventAggregator.PublishEvent(new PendingAuthorImportSucceededEvent(item, addedAuthor));
                        else
                            _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
                    }

                    return item;
                }

                _logger.Info("Successfully fulfilled pending import {0} (author ID: {1})", item.ProviderId, addedAuthor?.Id);
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
            catch (OperationCanceledException)
            {
                _pendingImportService.SaveProgress(item, progress);
                return _pendingImportService.GetByProviderId(item.ProviderId) ?? item;
            }
            catch (InvalidProviderIdException ex)
            {
                if (_pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, ex.Message, item.Version))
                    _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
            }
            catch (AuthorTerminalException ex)
            {
                var declaredReason = ex.Message;
                _logger.Warn("Pending author import {0} failed with declared terminal {1}; automatic retry stopped. Reopenable: {2}",
                    item.ProviderId, ex.Code, ex.Reopenable);
                if (_pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, declaredReason, item.Version))
                    _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
            }
            catch (WorkRescueTerminalException ex)
            {
                _logger.Warn("Pending book request {0} reached a declared terminal rescue state: {1}", ex.ProviderId, ex.Message);
                if (_pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, ex.Message, item.Version))
                    _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
            }

            catch (AuthorIdentityUnverifiedException ex)
            {
                _logger.Warn(ex, "Author identity for {0} is not yet verified; retaining the request for retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, ex.Message);
            }
            catch (AuthorNotFoundException)
            {
                // Author still not available, schedule retry
                _logger.Warn("Author {0} not yet available on metadata server, scheduling retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
            }
            catch (BookNotFoundException ex)
            {
                var reason = $"Requested book {ex.Message} is still being prepared by the metadata server.";
                _logger.Warn("Pending book request for author {0} is not ready; scheduling retry", item.ProviderId);
                _pendingImportService.ScheduleRetry(item, reason);
            }
            catch (ValidationException ex) when (ex.Errors.Any(error => error.ErrorCode == AuthorLibraryService.ImportListAuthorExcludedErrorCode))
            {
                // A newer direct request must survive an older list request's exclusion failure.
                if (_pendingImportService.UpdateStatus(item, PendingImportStatus.Failed, ex.Message, item.Version))
                {
                    _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(item));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing pending import {0}", item.Id);
                _pendingImportService.ScheduleRetry(item, ex.Message);
            }

            finally
            {
                _logger.Info("Pending import {0} attempt finished as {1}: {2} work lookups, {3} targets handed to the primary queue, {4} ms",
                    item.Id, item.OverallStatus, remoteWorkLookups, handedOffTargets, attemptTimer.ElapsedMilliseconds);
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
                SyncMonitoredAcrossFormats = item.SyncMonitoredAcrossFormats,
                IsAuthorSettingsRequest = item.IsAuthorSettingsRequest,
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
                RequestedBy = item.RequestedBy,
                ExplicitBookSearches = RequestedBookSearch.Read(item.ExplicitBookSearches),
                AudiobookRequestedBy = item.AudiobookRequestedBy,
                EbookRequestedBy = item.EbookRequestedBy,
                AuthorName = item.AuthorName,
                PendingImportId = item.Id,
                PendingImportVersion = item.Version,
                SearchForMissingBooks = item.SearchForMissingBooks,
                LastSelectedMediaType = item.LastSelectedMediaType,
                CreateAudiobook = item.HasAudiobook(),
                CreateEbook = item.HasEbook(),
                AudiobookBooksToAdd = DeserializeProviderIds(item.AudiobookBooksToAdd, nameof(item.AudiobookBooksToAdd)),
                AudiobookBooksToMonitor = audiobookMonitorTargets,
                AudiobookBooksToSearch = audiobookSearchTargets,
                AudiobookNarrations = string.IsNullOrEmpty(item.AudiobookNarrations) ? null : JsonConvert.DeserializeObject<List<RequestedNarration>>(item.AudiobookNarrations)?.Where(request => request != null).ToList(),
                EbookBooksToAdd = DeserializeProviderIds(item.EbookBooksToAdd, nameof(item.EbookBooksToAdd)),
                EbookBooksToMonitor = ebookMonitorTargets,
                EbookBooksToSearch = ebookSearchTargets
            };

            // Deserialize tags if present
            if (!string.IsNullOrEmpty(item.Tags))
            {
                config.Tags = JsonConvert.DeserializeObject<HashSet<int>>(item.Tags);
            }

            if (!string.IsNullOrEmpty(item.AudiobookTags))
            {
                config.AudiobookTags = JsonConvert.DeserializeObject<HashSet<int>>(item.AudiobookTags);
            }

            if (!string.IsNullOrEmpty(item.EbookTags))
            {
                config.EbookTags = JsonConvert.DeserializeObject<HashSet<int>>(item.EbookTags);
            }

            return config;
        }

        private MonitoringConfig BuildRoutedBookConfig(PendingAuthorImport item, Author primary, List<RequestedWork> targets, MonitoringConfig originalConfig)
        {
            var config = BuildMonitoringConfig(item);
            foreach (var target in targets.Where(target => IsImplicitRequestedSearch(item, originalConfig, target)))
            {
                target.Search = true;
                if (target.Narration != null) target.Narration.Search = true;
            }
            // Redirect only these works; the original author's catalog-wide demand
            // and discovery context must stay with its own request.
            config.AuthorName = primary.Name;
            config.DiscoveredAuthorFolderPath = null;
            config.SearchForMissingBooks = false;
            config.CreateAudiobook = targets.Any(target => target.MediaType == BookMediaType.Audiobook);
            config.CreateEbook = targets.Any(target => target.MediaType == BookMediaType.Ebook);
            config.AudiobookMonitorExistingMode = config.CreateAudiobook ? MonitorTypes.SpecificBook : null;
            config.EbookMonitorExistingMode = config.CreateEbook ? MonitorTypes.SpecificBook : null;
            config.AudiobookMonitorNewItems = config.CreateAudiobook ? NewItemMonitorTypes.None : null;
            config.EbookMonitorNewItems = config.CreateEbook ? NewItemMonitorTypes.None : null;
            config.AudiobookBooksToAdd = targets.Where(target => target.MediaType == BookMediaType.Audiobook && target.AddOnly).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.AudiobookBooksToMonitor = targets.Where(target => target.MediaType == BookMediaType.Audiobook && target.Monitor).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.EbookBooksToAdd = targets.Where(target => target.MediaType == BookMediaType.Ebook && target.AddOnly).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.EbookBooksToMonitor = targets.Where(target => target.MediaType == BookMediaType.Ebook && target.Monitor).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.AudiobookBooksToSearch = targets.Where(target => target.MediaType == BookMediaType.Audiobook && target.Search).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.EbookBooksToSearch = targets.Where(target => target.MediaType == BookMediaType.Ebook && target.Search).Select(target => target.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.AudiobookNarrations = targets.Where(target => target.Narration != null).Select(target => target.Narration).ToList();
            config.ExplicitBookSearches = config.ExplicitBookSearches.Where(request => targets.Any(target =>
                request.Matches(target.MediaType, target.ProviderId, target.EditionProviderId))).ToList();
            return config;
        }

        private static bool IsImplicitRequestedSearch(PendingAuthorImport item, MonitoringConfig config, RequestedWork target) =>
            item.SearchForMissingBooks && MonitoringConfig.IsImportListRequestOrigin(item.RequestedBy) &&
            (target.MediaType == BookMediaType.Audiobook ? config.AudiobookBooksToSearch == null : config.EbookBooksToSearch == null);

        private void ApplyRequestedMonitoring(PendingAuthorImport item, List<RequestedWork> requestedWorks)
        {
            var audiobookTargets = requestedWorks.Where(target => target.MediaType == BookMediaType.Audiobook && target.Monitor).ToList();
            var ebookTargets = requestedWorks.Where(target => target.MediaType == BookMediaType.Ebook && target.Monitor).ToList();

            var additions = requestedWorks.Where(target => target.AddOnly && target.Book != null).ToList();
            if (additions.Any())
            {
                _bookService.SetMonitored(additions.Select(target => target.Book.Id).Distinct(), false);
            }

            if (!audiobookTargets.Any() && !ebookTargets.Any() && !additions.Any())
            {
                return;
            }

            // A book-only request selects its exact rows without changing either author gate.
            var addOnlyIds = additions.Select(target => target.Book.Id).ToHashSet();
            var initialRequests = requestedWorks.Where(target => (target.Monitor || target.AddOnly) &&
                (MonitoringConfig.IsImportListRequestOrigin(item.RequestedBy) ||
                (target.MediaType == BookMediaType.Audiobook ? item.AudiobookMonitorExistingMode : item.EbookMonitorExistingMode).HasValue))
                .Select(target => target.Book).Where(book => book != null)
                .DistinctBy(book => book.Id)
                .Where(book => book.AddOptions?.AddType != BookAddType.Manual ||
                    book.AddOptions.MonitorBook != !addOnlyIds.Contains(book.Id)).ToList();
            foreach (var book in initialRequests)
            {
                book.AddOptions ??= new AddBookOptions();
                book.AddOptions.AddType = BookAddType.Manual;
                book.AddOptions.MonitorBook = !addOnlyIds.Contains(book.Id);
            }

            if (initialRequests.Any())
            {
                _bookService.SetAddOptions(initialRequests);
            }

            ApplyRequestedBookMonitoring(BookMediaType.Audiobook, item.HasAudiobook(), audiobookTargets);
            ApplyRequestedBookMonitoring(BookMediaType.Ebook, item.HasEbook(), ebookTargets);
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
                _bookService.MonitorRequestedBooks(
                    ids,
                    mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook");
            }
        }

        private static HashSet<int> GetRequestedSearchBookIds(Author author, List<RequestedWork> requestedWorks)
        {
            // Disabling the immediate search must not let a failed monitoring request report success.
            return requestedWorks
                .Where(target => target.Book != null)
                .Select(target =>
                {
                    var book = target.Book;
                    book.Author = target.Author ?? author;
                    if (target.Monitor && !book.IsMonitored())
                    {
                        throw new InvalidOperationException($"Requested {book.MediaType} book {book.Id} remained unmonitored after applying the saved request monitoring.");
                    }

                    return target;
                })
                .Where(target => target.Search && !target.NarrationUnavailable)
                .Select(target => target.Book.Id)
                .ToHashSet();
        }

        private List<Exception> QueueRequestedSearches(PendingAuthorImport item, Author author, List<Book> books, List<RequestedWork> requestedWorks, Action saveProgress)
        {
            var failures = new List<Exception>();
            // Author setup and list imports persist aggregate search with initial monitoring.
            // The scan handler completes monitoring and reconciliation before enqueuing it.
            if (author != null && item.SearchForMissingBooks && item.IsAuthorSettingsRequest)
            {
                if (author.AddOptions?.SearchForMissingBooks != true)
                    throw new InvalidOperationException("The author search was not retained for the post-import scan.");
                item.SearchForMissingBooks = false;
                saveProgress();
            }
            if (author != null && item.SearchForMissingBooks && !MonitoringConfig.IsImportListRequestOrigin(item.RequestedBy))
            {
                try
                {
                    _commandQueueManager.Push(new MissingBookSearchCommand { AuthorId = author.Id });
                    item.SearchForMissingBooks = false;
                    saveProgress();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(ex); _logger.Warn(ex, "Missing-book search for author {0} was rejected", author.Id); }
                _logger.Debug("Queued missing book search for imported author {0} (ID: {1})", item.ProviderId, author.Id);
            }

            if (!books.Any()) return failures;

            var explicitSearches = RequestedBookSearch.Read(item.ExplicitBookSearches);
            var explicitIds = requestedWorks.Where(target => target.Search && explicitSearches.Any(request =>
                request.Matches(target.MediaType, target.ProviderId, target.EditionProviderId)))
                .Select(target => target.Book?.Id ?? 0).ToHashSet();
            // A still-held initial author search owns automatic rows on its monitored media sides.
            // Acknowledge that durable delegation so later retries and row markers cannot repeat it.
            var delegated = books.Where(book => !explicitIds.Contains(book.Id) && book.Author.AddOptions?.SearchForMissingBooks == true && book.IsMonitoredWithAuthor()).ToList();
            var queueTargets = books.Except(delegated).ToList();
            var automatic = queueTargets.Where(book => !explicitIds.Contains(book.Id)).ToList();
            var immediate = queueTargets.Except(automatic).ToList();
            if (automatic.Any())
            {
                // Keep search intent on the selected rows until the queue accepts it. Repeated
                // list entries can then skip unchanged narrations without losing a failed push.
                foreach (var book in automatic)
                {
                    book.AddOptions ??= new AddBookOptions();
                    book.AddOptions.SearchForNewBook = true;
                }
                _bookService.SetAddOptions(automatic);
            }

            void AcknowledgeSearches(List<Book> acknowledged)
            {
                if (acknowledged.Count == 0) return;
                var acknowledgedIds = acknowledged.Select(book => book.Id).ToHashSet();
                var accepted = requestedWorks.Where(target => acknowledgedIds.Contains(target.Book?.Id ?? 0)).ToList();
                accepted.ForEach(target => target.Search = false);
                var audio = accepted.Where(target => target.MediaType == BookMediaType.Audiobook).Select(target => target.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ebook = accepted.Where(target => target.MediaType == BookMediaType.Ebook).Select(target => target.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (audio.Any())
                {
                    item.AudiobookBooksToSearch = JsonConvert.SerializeObject((DeserializeProviderIds(item.AudiobookBooksToSearch, nameof(item.AudiobookBooksToSearch)) ?? new List<string>()).Where(id => !audio.Contains(id)));
                }
                if (ebook.Any())
                {
                    item.EbookBooksToSearch = JsonConvert.SerializeObject((DeserializeProviderIds(item.EbookBooksToSearch, nameof(item.EbookBooksToSearch)) ?? new List<string>()).Where(id => !ebook.Contains(id)));
                }
                if (accepted.Count > 0 && !string.IsNullOrEmpty(item.AudiobookNarrations))
                {
                    var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(item.AudiobookNarrations) ?? new List<RequestedNarration>();
                    foreach (var narration in narrations.Where(narration => narration != null && accepted.Any(target => target.EditionProviderId != null &&
                        target.MediaType == narration.MediaType &&
                        string.Equals(target.ProviderId, narration.WorkProviderId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(target.EditionProviderId, narration.EditionProviderId, StringComparison.OrdinalIgnoreCase)))) narration.Search = false;
                    item.AudiobookNarrations = JsonConvert.SerializeObject(narrations);
                }
                PruneAppliedTargets(item, accepted, monitoring: false);
                saveProgress();
            }

            var queued = new List<Book>();
            try
            {
                AcknowledgeSearches(delegated);
                if (automatic.Any())
                {
                    try
                    {
                        _commandQueueManager.Push(new BookSearchCommand(automatic.Select(book => book.Id).ToList()));
                        queued.AddRange(automatic);
                        AcknowledgeSearches(automatic);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add(ex); _logger.Warn(ex, "Automatic search for author {0} was rejected", author?.Id); }
                }
                foreach (var book in immediate)
                {
                    try
                    {
                        _commandQueueManager.Push(new BookSearchCommand(new List<int> { book.Id }), trigger: BookSearchCommand.SearchOnAddTrigger(book.Author, book.MediaType));
                        queued.Add(book);
                        AcknowledgeSearches(new List<Book> { book });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add(ex); _logger.Warn(ex, "Requested search for book {0} was rejected", book.Id); }
                }
            }
            finally
            {
                var acknowledged = queued.Concat(delegated).ToList();
                var heldAccepted = acknowledged.Where(book => book.AddOptions?.SearchForNewBook == true).ToList();
                if (heldAccepted.Count > 0)
                {
                    heldAccepted.ForEach(book => book.AddOptions.SearchForNewBook = false);
                    _bookService.SetAddOptions(heldAccepted);
                }
                _logger.Debug("Requested searches for author {0}: {1} automatic targets, {2} immediate targets, {3} queued, {4} delegated, {5} held",
                    author?.Id, automatic.Count, immediate.Count, queued.Count, delegated.Count, books.Count - acknowledged.Count);
            }
            return failures;
        }

        private static string ExcludedAuthorReason(string providerId, BookMediaType mediaType) =>
            $"Author {providerId} is excluded from import lists for {mediaType}.";

        private static void MarkExcludedRequests(IEnumerable<RequestedWork> targets, MonitoringConfig config, string providerId)
        {
            foreach (var target in targets.Where(target => target.MediaType == BookMediaType.Audiobook
                         ? !config.CreateAudiobook : !config.CreateEbook))
            {
                target.Failure = ExcludedAuthorReason(providerId, target.MediaType);
            }
        }

        private List<RequestedWork> GetRequestedWorks(MonitoringConfig config)
        {
            var audiobookSearch = (config.CreateAudiobook ? config.AudiobookBooksToSearch : null) ?? new List<string>();
            var ebookSearch = (config.CreateEbook ? config.EbookBooksToSearch : null) ?? new List<string>();
            var requested = new List<RequestedWork>();

            AddRequestedWorks(requested, BookMediaType.Audiobook,
                MergeProviderIds(config.CreateAudiobook ? config.AudiobookBooksToMonitor : null, config.CreateAudiobook ? config.AudiobookBooksToAdd : null, audiobookSearch),
                audiobookSearch, config.AudiobookBooksToMonitor, config.AudiobookBooksToAdd);
            AddRequestedWorks(requested, BookMediaType.Ebook,
                MergeProviderIds(config.CreateEbook ? config.EbookBooksToMonitor : null, config.CreateEbook ? config.EbookBooksToAdd : null, ebookSearch),
                ebookSearch, config.EbookBooksToMonitor, config.EbookBooksToAdd);

            var narrations = (config.AudiobookNarrations ?? new List<RequestedNarration>())
                .Where(request => request.MediaType == BookMediaType.Audiobook ? config.CreateAudiobook : config.CreateEbook).ToList();
            var genericSearches = audiobookSearch.Select(id => (BookMediaType.Audiobook, id.ToLowerInvariant()))
                .Concat(ebookSearch.Select(id => (BookMediaType.Ebook, id.ToLowerInvariant()))).ToHashSet();
            foreach (var narration in narrations)
            {
                var genericSearch = genericSearches.Remove((narration.MediaType, narration.WorkProviderId.ToLowerInvariant()));
                var addOnly = (narration.MediaType == BookMediaType.Audiobook ? config.AudiobookBooksToAdd : config.EbookBooksToAdd)
                    ?.Contains(narration.WorkProviderId, StringComparer.OrdinalIgnoreCase) == true;
                requested.Add(new RequestedWork
                {
                    AddOnly = addOnly,
                    ProviderId = narration.WorkProviderId,
                    Narration = narration,
                    MediaType = narration.MediaType,
                    Search = narration.Search || genericSearch,
                    Monitor = !addOnly && ((narration.MediaType == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor)
                        ?.Contains(narration.WorkProviderId, StringComparer.OrdinalIgnoreCase) ?? true)
                });
            }

            // An edition request replaces that work's generic target, otherwise the generic
            // target could select and search a different narration as well.
            var narratedWorks = narrations.Select(request => (request.MediaType, request.WorkProviderId.ToLowerInvariant())).ToHashSet();
            requested.RemoveAll(request => request.EditionProviderId == null && narratedWorks.Contains((request.MediaType, request.ProviderId.ToLowerInvariant())) &&
                config.ExplicitBookSearches?.Any(search => search.Matches(request.MediaType, request.ProviderId, null)) != true);
            return requested;
        }

        private static void AddRequestedWorks(List<RequestedWork> requested, BookMediaType mediaType, List<string> providerIds, List<string> searchIds, List<string> monitorIds, List<string> addIds)
        {
            foreach (var providerId in providerIds ?? new List<string>())
            {
                requested.Add(new RequestedWork
                {
                    ProviderId = providerId,
                    MediaType = mediaType,
                    Search = searchIds.Contains(providerId, StringComparer.OrdinalIgnoreCase),
                    AddOnly = addIds?.Contains(providerId, StringComparer.OrdinalIgnoreCase) == true &&
                        monitorIds?.Contains(providerId, StringComparer.OrdinalIgnoreCase) != true,
                    Monitor = monitorIds?.Contains(providerId, StringComparer.OrdinalIgnoreCase) == true
                });
            }
        }

        private void ResolveLocalBooks(List<RequestedWork> requestedWorks, Author author, MonitoringConfig config, List<Book> catalog)
        {
            catalog ??= new List<Book>();
            var byId = catalog.Where(book => book.AuthorId == author.Id).ToDictionary(book => book.Id);
            var aliases = _providerAliases?.FindBookAliases("work", requestedWorks.Select(target => target.ProviderId)) ?? new List<ProviderAlias>();
            var workLookup = catalog.SelectMany(book => BookIdentity.GetStableWorkProviderIdentityTokens(book)
                    .Select(id => (book, id: ProviderIdHelper.Normalize(id, null).ToLowerInvariant())))
                .Concat(aliases.Where(alias => byId.ContainsKey(alias.EntityId)).Select(alias =>
                    (book: byId[alias.EntityId], id: $"{alias.Provider}:{alias.NormalizedProviderId}".ToLowerInvariant())))
                .ToLookup(value => (value.book.MediaType, value.id), value => value.book);
            foreach (var target in requestedWorks)
            {
                string normalized;
                try
                {
                    normalized = ProviderIdHelper.Normalize(target.ProviderId, defaultPrefix: null);
                }
                catch (InvalidOperationException ex)
                {
                    target.Failure = ex.Message;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    target.Failure = $"Invalid requested work provider ID '{target.ProviderId}'.";
                    continue;
                }
                var matches = workLookup[(target.MediaType, normalized.ToLowerInvariant())].Where(book => book.AuthorId == author.Id).DistinctBy(book => book.Id).ToList();
                target.Book = BookIdentity.SelectRequestedBook(matches);
                target.Author = author;
                target.Edition = null;
                target.NarrationUnavailable = false;
                if (target.EditionProviderId == null) continue;
                if (!target.Monitor && !target.AddOnly)
                {
                    target.Book = BookIdentity.SelectRequestedBook(matches.Where(book => MatchesCurrentRequestedSelection(book, target))) ?? target.Book;
                    continue;
                }
                var editionKey = BookEditionIdentity.NormalizeRequestedEditionProviderId(target.EditionProviderId);
                var edition = BookEditionIdentity.OrderRequestedNarrationEditions(matches.SelectMany(book => book.Editions ?? new List<Edition>())
                    .Where(candidate => target.MediaType == BookMediaType.Ebook
                        ? BookEditionIdentity.EditionMatchesProviderId(candidate, target.EditionProviderId)
                        : BookEditionIdentity.MatchesRequestedNarration(candidate, target.EditionProviderId, target.NarratorNames) &&
                            (!target.RequiresEdition || BookEditionIdentity.IsNarratedAudiobookEdition(candidate)))).FirstOrDefault();
                var knownUnavailable = edition == null && target.RequiresEdition &&
                    config.KnownParentEditionRequests?.Contains((target.MediaType, editionKey?.ToLowerInvariant())) == true;
                if (target.MediaType == BookMediaType.Ebook && knownUnavailable)
                {
                    target.Failure = UnavailableEditionReason(target);
                    target.Book = null;
                    continue;
                }
                target.NarrationUnavailable = knownUnavailable;
                if (edition == null && target.RequiresEdition && !target.NarrationUnavailable)
                {
                    target.Book = null;
                    continue;
                }
                if (target.MediaType == BookMediaType.Ebook || BookEditionIdentity.IsNarratedAudiobookEdition(edition))
                    target.Edition = edition;
            }
        }

        private static bool MatchesCurrentRequestedSelection(Book book, RequestedWork target)
        {
            if (target.Narration == null) return true;
            var edition = BookEditionIdentity.GetMonitoredEdition(book);
            return edition != null && (target.MediaType == BookMediaType.Ebook
                ? BookEditionIdentity.EditionMatchesProviderId(edition, target.EditionProviderId)
                : BookEditionIdentity.MatchesRequestedNarration(edition, target.EditionProviderId, target.NarratorNames));
        }

        private static string UnavailableEditionReason(RequestedWork target) =>
            $"Requested {target.MediaType} edition {target.EditionProviderId} is unavailable in the receiving author's profile-filtered catalog; the edition request was not fulfilled. Automatic retries stopped. A later list refresh can request it again.";

        private List<Exception> PinRequestedNarrations(List<RequestedWork> requestedWorks, MonitoringConfig config, List<Book> catalog)
        {
            var failures = new List<Exception>();
            var targets = requestedWorks.Where(target => (target.Monitor || target.AddOnly) && target.Edition != null).ToList();
            if (targets.Count == 0) return failures;
            var previousSelections = catalog.ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
            var targetsByEdition = targets.ToLookup(target => target.Edition.Id);
            var selected = _bookService.AddWantedEditions(targets.Select(target => target.Edition).DistinctBy(edition => edition.Id).ToList(), (edition, error) =>
            {
                if (error is ValidationException)
                {
                    foreach (var target in targetsByEdition[edition.Id]) target.Failure = $"Requested edition {target.EditionProviderId}: {error.Message}";
                }
                else
                {
                    failures.Add(error);
                }
                _logger.Warn(error, "Requested narration on edition {0} could not be selected", edition.Id);
            });
            var changedCovers = selected.Values.Where(book => previousSelections.GetValueOrDefault(book.Id) != BookEditionIdentity.GetMonitoredEdition(book)?.Id)
                .Select(book => book.Id).Distinct().ToList();
            foreach (var target in targets)
            {
                if (!selected.TryGetValue(target.Edition.Id, out var book))
                {
                    target.Book = null;
                    if (target.Failure == null && failures.Count == 0)
                    {
                        failures.Add(new InvalidOperationException($"Requested narration {target.EditionProviderId} could not be selected."));
                    }
                    continue;
                }
                if (MonitoringConfig.IsImportListRequestOrigin(config.RequestedBy) && config.PreviouslyMonitoredEditions?.TryGetValue(book.Id, out var previousEdition) == true &&
                    previousEdition == BookEditionIdentity.GetMonitoredEdition(book)?.Id && book.AddOptions?.SearchForNewBook != true)
                    target.Search = false;
                target.Book = book;
            }
            var held = targets.Where(target => target.Search && target.Book != null).Select(target => target.Book).DistinctBy(book => book.Id).ToList();
            foreach (var book in held)
            {
                book.AddOptions ??= new AddBookOptions();
                book.AddOptions.SearchForNewBook = true;
            }
            if (held.Count > 0) _bookService.SetAddOptions(held);
            if (changedCovers.Count > 0)
            {
                try { _commandQueueManager.Push(new DownloadBookMediaCommand(changedCovers)); }
                catch (Exception ex) { failures.Add(ex); _logger.Warn(ex, "Requested narration media download was rejected"); }
            }
            return failures;
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
            public RequestedNarration Narration { get; init; }
            public string EditionProviderId => Narration?.EditionProviderId;
            public List<string> NarratorNames => Narration?.NarratorNames;
            public bool RequiresEdition => Narration?.RequiresEdition == true;
            public bool NarrationUnavailable { get; set; }
            public Edition Edition { get; set; }
            public string Failure { get; set; }
            public BookMediaType MediaType { get; init; }
            public bool Search { get; set; }
            public bool AddOnly { get; init; }
            public bool Monitor { get; init; }
            public Book Book { get; set; }
            public Author Author { get; set; }
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

        private static PendingAuthorImport SnapshotProgress(PendingAuthorImport item) => new()
        {
            AudiobookBooksToAdd = item.AudiobookBooksToAdd, AudiobookBooksToMonitor = item.AudiobookBooksToMonitor, EbookBooksToAdd = item.EbookBooksToAdd, EbookBooksToMonitor = item.EbookBooksToMonitor,
            AudiobookBooksToSearch = item.AudiobookBooksToSearch, EbookBooksToSearch = item.EbookBooksToSearch,
            AudiobookNarrations = item.AudiobookNarrations, SearchForMissingBooks = item.SearchForMissingBooks,
            ExplicitBookSearches = item.ExplicitBookSearches
        };

        private static bool HasIndependentAuthorDemand(MonitoringConfig config, List<RequestedWork> works) =>
            config.SearchForMissingBooks == true ||
            !string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath) ||
            new[] { BookMediaType.Audiobook, BookMediaType.Ebook }.Any(media =>
                (media == BookMediaType.Audiobook ? config.CreateAudiobook : config.CreateEbook) &&
                (!works.Any(target => target.MediaType == media) ||
                 (media == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode) is MonitorTypes mode &&
                 mode != MonitorTypes.SpecificBook && mode != MonitorTypes.None));

        private void PruneAppliedTargets(PendingAuthorImport item, List<RequestedWork> targets, bool monitoring, bool searches = false)
        {
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                var ids = targets.Where(target => target.MediaType == media).Select(target => target.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (ids.Count == 0) continue;
                // Keep an explicitly unmonitored target until its optional manual search is accepted.
                // It must not become a withdrawn monitoring request when the queue retries.
                var completedAddIds = targets.Where(target => target.MediaType == media && !target.Search)
                    .Select(target => target.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var addJson = media == BookMediaType.Audiobook ? item.AudiobookBooksToAdd : item.EbookBooksToAdd;
                if (DeserializeProviderIds(addJson, "BooksToAdd") is List<string> additions)
                {
                    addJson = JsonConvert.SerializeObject(additions.Where(id => !completedAddIds.Contains(id)));
                    if (media == BookMediaType.Audiobook) item.AudiobookBooksToAdd = addJson;
                    else item.EbookBooksToAdd = addJson;
                }
                if (monitoring)
                {
                    var json = media == BookMediaType.Audiobook ? item.AudiobookBooksToMonitor : item.EbookBooksToMonitor;
                    var saved = DeserializeProviderIds(json, "BooksToMonitor");
                    if (saved != null || targets.Any(target => target.MediaType == media && target.Monitor && target.Narration != null))
                    {
                        json = JsonConvert.SerializeObject((saved ?? new List<string>()).Where(id => !ids.Contains(id)));
                        if (media == BookMediaType.Audiobook) item.AudiobookBooksToMonitor = json;
                        else item.EbookBooksToMonitor = json;
                    }
                }
                if (searches)
                {
                    var json = media == BookMediaType.Audiobook ? item.AudiobookBooksToSearch : item.EbookBooksToSearch;
                    if (DeserializeProviderIds(json, "BooksToSearch") is List<string> saved)
                    {
                        json = JsonConvert.SerializeObject(saved.Where(id => !ids.Contains(id)));
                        if (media == BookMediaType.Audiobook) item.AudiobookBooksToSearch = json;
                        else item.EbookBooksToSearch = json;
                    }
                }
            }
            if (searches || !monitoring)
                item.ExplicitBookSearches = RequestedBookSearch.Write(RequestedBookSearch.Read(item.ExplicitBookSearches)
                    .Where(request => !targets.Any(target => request.Matches(target.MediaType, target.ProviderId, target.EditionProviderId))));
            if (!monitoring && item.AudiobookNarrations != null)
                item.AudiobookNarrations = JsonConvert.SerializeObject((JsonConvert.DeserializeObject<List<RequestedNarration>>(item.AudiobookNarrations) ?? new())
                    .Where(request => request != null && !targets.Any(target => target.Narration == request ||
                        target.MediaType == request.MediaType && string.Equals(target.ProviderId, request.WorkProviderId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(target.EditionProviderId, request.EditionProviderId, StringComparison.OrdinalIgnoreCase))));
        }
    }
}
