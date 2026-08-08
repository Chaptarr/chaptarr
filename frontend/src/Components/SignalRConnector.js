import * as signalR from '@microsoft/signalr/dist/browser/signalr.js';
import PropTypes from 'prop-types';
import { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { getDefaultImportTracker, hideMessage, setAppValue, setVersion, showMessage } from 'Store/Actions/appActions';
import { fetchAuthor } from 'Store/Actions/authorActions';
import { showAuthorFolderPicker } from 'Store/Actions/authorFolderActions';
import { removeItem, update, updateItem } from 'Store/Actions/baseActions';
import { deleteAuthorBooks } from 'Store/Actions/bookActions';
import { fetchCommands, finishCommand, pauseCommand, resumeCommand, updateCommand } from 'Store/Actions/commandActions';
import { fetchQueue, fetchQueueDetails } from 'Store/Actions/queueActions';
import { fetchQualityDefinitions, fetchRootFolders } from 'Store/Actions/settingsActions';
import { fetchHealth } from 'Store/Actions/systemActions';
import { fetchTagDetails, fetchTags } from 'Store/Actions/tagActions';
import { detectOrphanedModals, forceCloseAllModals } from 'Utilities/modalCleanup';
import { repopulatePage } from 'Utilities/pagePopulator';
import titleCase from 'Utilities/String/titleCase';

function getHandlerName(name) {
  name = titleCase(name);
  name = name.replace('/', '');

  return `handle${name}`;
}

function isImportProgressCommand(command) {
  return command &&
    (command.name === 'RescanFolders' ||
      command.name === 'BulkRefreshAuthor' ||
      command.name === 'RefreshUnmappedFiles' ||
      command.name === 'RetryUnmappedMatch') &&
    (command.status === 'started' || command.status === 'paused');
}

const IMPORT_PROGRESS_UPDATE_INTERVAL_MS = 100;

function createMapStateToProps() {
  return createSelector(
    (state) => state.app.isReconnecting,
    (state) => state.app.isDisconnected,
    (state) => state.queue.paged.isPopulated,
    (state) => state.commands.items,
    (state) => state.app.importTracker,
    (isReconnecting, isDisconnected, isQueuePopulated, commands, importTracker) => {
      return {
        isReconnecting,
        isDisconnected,
        isQueuePopulated,
        commands,
        importTracker
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchCommands: fetchCommands,
  dispatchUpdateCommand: updateCommand,
  dispatchFinishCommand: finishCommand,
  dispatchPauseCommand: pauseCommand,
  dispatchResumeCommand: resumeCommand,
  dispatchSetAppValue: setAppValue,
  dispatchSetVersion: setVersion,
  dispatchUpdate: update,
  dispatchUpdateItem: updateItem,
  dispatchRemoveItem: removeItem,
  dispatchDeleteAuthorBooks: deleteAuthorBooks,
  dispatchFetchAuthor: fetchAuthor,
  dispatchFetchHealth: fetchHealth,
  dispatchFetchQualityDefinitions: fetchQualityDefinitions,
  dispatchFetchQueue: fetchQueue,
  dispatchFetchQueueDetails: fetchQueueDetails,
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchFetchTags: fetchTags,
  dispatchFetchTagDetails: fetchTagDetails,
  dispatchShowAuthorFolderPicker: showAuthorFolderPicker,
  dispatchShowMessage: showMessage,
  dispatchHideMessage: hideMessage
};

function Logger(minimumLogLevel) {
  this.minimumLogLevel = minimumLogLevel;
}

Logger.prototype.cleanse = function(message) {
  const apikey = new RegExp(`access_token=${encodeURIComponent(window.Chaptarr.apiKey)}`, 'g');
  return message.replace(apikey, 'access_token=(removed)');
};

Logger.prototype.log = function(logLevel, message) {
  // see https://github.com/aspnet/AspNetCore/blob/21c9e2cc954c10719878839cd3f766aca5f57b34/src/SignalR/clients/ts/signalr/src/Utils.ts#L147
  if (logLevel >= this.minimumLogLevel) {
    switch (logLevel) {
      case signalR.LogLevel.Critical:
      case signalR.LogLevel.Error:
        console.error(`[signalR] ${signalR.LogLevel[logLevel]}: ${this.cleanse(message)}`);
        break;
      case signalR.LogLevel.Warning:
        console.warn(`[signalR] ${signalR.LogLevel[logLevel]}: ${this.cleanse(message)}`);
        break;
      case signalR.LogLevel.Information:
        console.info(`[signalR] ${signalR.LogLevel[logLevel]}: ${this.cleanse(message)}`);
        break;
      default:
        // console.debug only goes to attached debuggers in Node, so we use console.log for Trace and Debug
        console.log(`[signalR] ${signalR.LogLevel[logLevel]}: ${this.cleanse(message)}`);
        break;
    }
  }
};

class SignalRConnector extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.connection = null;
    this.pendingImportProgress = null;
    this.importProgressUpdateTimer = null;
    this.state = {
      importCommandId: null,
      isPaused: false,
      latestProgressData: null
    };
  }

  componentDidMount() {
    console.log('[signalR] starting');
    
    // Try to restore import state on mount
    this.restoreImportState();
    this.importTrackerCleanupInterval = setInterval(this.cleanupStaleImportState, 5000);

    const url = `${window.Chaptarr.urlBase}/signalr/messages`;

    this.connection = new signalR.HubConnectionBuilder()
      .configureLogging(new Logger(signalR.LogLevel.Information))
      .withUrl(`${url}?access_token=${encodeURIComponent(window.Chaptarr.apiKey)}`)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.elapsedMilliseconds > 180000) {
            this.props.dispatchSetAppValue({ isDisconnected: true });
          }
          return Math.min(retryContext.previousRetryCount, 10) * 1000;
        }
      })
      .build();

    this.connection.onreconnecting(this.onReconnecting);
    this.connection.onreconnected(this.onReconnected);
    this.connection.onclose(this.onClose);

    this.connection.on('receiveMessage', this.onReceiveMessage);

    this.connection.start().then(this.onStart, this.onStartFail);
  }

  componentWillUnmount() {
    if (this.importTrackerCleanupInterval) {
      clearInterval(this.importTrackerCleanupInterval);
      this.importTrackerCleanupInterval = null;
    }
    this.flushPendingImportProgress();

    this.connection.stop();
    this.connection = null;
  }

  //
  // Control

  missingHandlersLogged = new Set();
  
  handlePauseResume = () => {
    const { isPaused } = this.state;
    const { commands } = this.props;
    
    // Find the active RescanFolders command
    const activeImportCommand = commands && commands.find(cmd => 
      cmd.name === 'RescanFolders' && 
      (cmd.status === 'started' || cmd.status === 'paused')
    );
    
    if (!activeImportCommand) {
      console.warn('[SignalR] Cannot pause/resume - no active RescanFolders command found');
      return;
    }
    
    const commandId = activeImportCommand.id;
    const newPausedState = !isPaused;
    this.setState({ isPaused: newPausedState, importCommandId: commandId });
    
    console.log('[SignalR] Import', newPausedState ? 'pausing' : 'resuming', 'command:', commandId);
    
    // Actually pause/resume the backend command
    if (newPausedState) {
      this.props.dispatchPauseCommand({ id: commandId });
    } else {
      this.props.dispatchResumeCommand({ id: commandId });
    }
    
    // Update the visual state immediately for responsive UI
    const { latestProgressData } = this.state;
    if (latestProgressData) {
      this.props.dispatchShowMessage({
        ...latestProgressData,
        isPaused: newPausedState,
        onPauseResume: this.handlePauseResume
      });
    }
  };
  
  handleMessage = (message) => {
    try {
      const {
        name,
        body
      } = message;

      if (!name) {
        return;
      }

      const handlerName = getHandlerName(name);
      const handler = this[handlerName];

      if (handler) {
        handler(body);
        return;
      }

      if (!this.missingHandlersLogged.has(name)) {
        this.missingHandlersLogged.add(name);
        console.warn(`[signalR] No handler for '${name}' (${handlerName})`);
      }
    } catch (error) {
      console.error(`signalR: Error handling message ${message?.name}:`, error);
      // Avoid dumping large payloads and/or filesystem paths to the console
      console.error('signalR: Message action:', message?.body?.action);
      // Don't re-throw to prevent crashing the entire app
    }
  };

  handleCalendar = (body) => {
    if (body && body.action === 'updated' && body.resource) {
      this.props.dispatchUpdateItem({
        section: 'calendar',
        updateOnly: true,
        ...body.resource
      });
    }
  };

  handleCommand = (body) => {
    if (!body || !body.action) {
      return;
    }

    if (body.action === 'sync') {
      this.props.dispatchFetchCommands();
      return;
    }

    if (!body.resource) {
      // Some broadcasts (e.g. Deleted/Sync) may not include a resource.
      return;
    }

    const resource = body.resource;
    const status = resource.status;
    const name = resource.name;

    // Both sucessful and failed commands need to be
    // completed, otherwise they spin until they timeout.

    if (status === 'completed' || status === 'failed') {
      this.props.dispatchFinishCommand(resource);

      // If an import/scan command completed, clear the header import tracker and any persisted state.
      // ImportStageProgress can be emitted from multiple parts of the pipeline; command completion is the
      // single reliable signal that all queued work for that run is done.
      if (name === 'RescanFolders' || name === 'BulkRefreshAuthor' || name === 'RefreshUnmappedFiles' || name === 'RetryUnmappedMatch') {
        this.clearPendingImportProgress();
        this.props.dispatchSetAppValue({ importTracker: getDefaultImportTracker() });
        localStorage.removeItem('importProgress');
      }

      if (name === 'BulkRefreshAuthor') {
        this.props.dispatchHideMessage({ id: `bulk-author-book-progress-${resource.id}` });
      }
    } else {
      this.props.dispatchUpdateCommand(resource);
    }
  };

  handleBook = (body) => {
    if (!body || !body.action) {
      return;
    }

    const action = body.action;
    const section = 'books';

    if (action === 'sync') {
      repopulatePage('bookSync');
      return;
    }

    if (!body.resource || !body.resource.id) {
      // Some broadcasts (e.g. Deleted without resource) are informational only.
      return;
    }

    if (action === 'updated') {
      this.props.dispatchUpdateItem({
        section,
        ...body.resource
      });
    } else if (action === 'deleted') {
      this.props.dispatchRemoveItem({
        section,
        id: body.resource.id
      });
    }
  };

  handleBookfile = (body) => {
    const section = 'bookFiles';

    if (!body || !body.action) {
      return;
    }

    if (body.action === 'sync') {
      repopulatePage('bookFileSync');
      return;
    }

    if (!body.resource || !body.resource.id) {
      return;
    }

    if (body.action === 'updated') {
      this.props.dispatchUpdateItem({ section, ...body.resource });
      
      // Only repopulate page for newly imported files or significant updates
      // This prevents continuous refreshing when unrelated files are processed
      if (body.resource.qualityCutoffNotMet !== undefined || 
          body.updateReason === 'imported' || 
          body.updateReason === 'upgraded') {
        repopulatePage('bookFileUpdated');
      }
    } else if (body.action === 'deleted') {
      this.props.dispatchRemoveItem({ section, id: body.resource.id });
      repopulatePage('bookFileDeleted');
    }
  };

  handleHealth = () => {
    this.props.dispatchFetchHealth();
  };

  handleAuthor = (body) => {
    const section = 'authors';

    // Defensive programming: validate SignalR message data
    if (!body || !body.action) {
      console.warn('[signalR] Received invalid author message - missing action');
      return;
    }

    const action = body.action;

    if (action === 'sync') {
      this.props.dispatchFetchAuthor();
      return;
    }

    if (action === 'updated') {
      // Validate resource data before dispatching
      if (!body.resource || typeof body.resource !== 'object') {
        console.warn('[signalR] Received author update with invalid resource data, skipping');
        return;
      }

      // Ensure required fields exist to prevent charAt errors
      if (!body.resource.id) {
        console.warn('[signalR] Received author update without ID, skipping');
        return;
      }

      this.props.dispatchUpdateItem({ section, ...body.resource });
    } else if (action === 'deleted') {
      // Deleted broadcasts may be sent without a resource (informational only)
      if (!body.resource || !body.resource.id) {
        return;
      }

      this.props.dispatchRemoveItem({ section, id: body.resource.id });
      this.props.dispatchDeleteAuthorBooks({ authorId: body.resource.id });
    }
  };

  handleQualitydefinition = () => {
    this.props.dispatchFetchQualityDefinitions();
  };

  handleQueue = () => {
    if (this.props.isQueuePopulated) {
      this.props.dispatchFetchQueue();
    }
  };

  handleQueueDetails = () => {
    this.props.dispatchFetchQueueDetails();
  };

  handleQueueStatus = (body) => {
    if (!body || !body.resource) {
      return;
    }

    this.props.dispatchUpdate({ section: 'queue.status', data: body.resource });
  };

  handleVersion = (body) => {
    if (!body || !body.version) {
      return;
    }

    const version = body.version;

    this.props.dispatchSetVersion({ version });
  };

  handleWantedCutoff = (body) => {
    if (body && body.action === 'updated' && body.resource) {
      this.props.dispatchUpdateItem({
        section: 'wanted.cutoffUnmet',
        updateOnly: true,
        ...body.resource
      });
    }
  };

  handleWantedMissing = (body) => {
    if (body && body.action === 'updated' && body.resource) {
      this.props.dispatchUpdateItem({
        section: 'wanted.missing',
        updateOnly: true,
        ...body.resource
      });
    }
  };

  handleSystemTask = () => {
    this.props.dispatchFetchCommands();
  };

  handleRootfolder = (body) => {
    // Defensive programming: validate SignalR message data
    if (!body || !body.action) {
      console.warn('[signalR] Received invalid rootfolder message - missing action');
      return;
    }

    if (body.action === 'updated') {
      // Validate resource data before dispatching
      if (!body.resource || typeof body.resource !== 'object') {
        console.warn('[signalR] Received rootfolder update with invalid resource data, skipping');
        return;
      }

      this.props.dispatchUpdateItem({
        section: 'settings.rootFolders',
        updateOnly: true,
        ...body.resource
      });
    }
  };

  handleTag = (body) => {
    if (!body || !body.action) {
      return;
    }

    if (body.action === 'sync') {
      this.props.dispatchFetchTags();
      this.props.dispatchFetchTagDetails();
      return;
    }
  };

  handleAuthorfoldermatchfound = (body) => {
    if (body && body.resource) {
      this.props.dispatchShowAuthorFolderPicker({
        authorId: body.resource.authorId,
        authorName: body.resource.authorName,
        rootFolderId: body.resource.rootFolderId,
        matches: body.resource.matches
      });
    }
  };

  // This handler was a duplicate with wrong casing - removed to avoid confusion

  handleAuthordiscoveryprogress = (body) => {
    // Consolidated into main import progress - no longer show separate message
    // This data is now included in the ImportStageProgress event
    console.debug('[SignalR] Author discovery progress consolidated into main import tracker');
  };

  handleAuthorimportprogress = (body) => {
    // Consolidated into main import progress - no longer show separate message
    // This data is now included in the ImportStageProgress event
    console.debug('[SignalR] Author import progress consolidated into main import tracker');
  };

  handleBulkauthorbookprogress = (body) => {
    if (!body || !body.commandId || !body.message) {
      return;
    }

    this.props.dispatchShowMessage({
      id: `bulk-author-book-progress-${body.commandId}`,
      commandId: body.commandId,
      name: 'BulkAuthorBookProgress',
      message: body.message,
      type: 'info',
      hideAfter: 0
    });
  };

  // Track active pending import notifications to prevent UI clutter
  pendingImportNotifications = new Set();
  flushPendingImportProgress = () => {
    if (this.importProgressUpdateTimer) {
      clearTimeout(this.importProgressUpdateTimer);
      this.importProgressUpdateTimer = null;
    }

    const pending = this.pendingImportProgress;
    this.pendingImportProgress = null;

    if (!pending) {
      return;
    }

    this.props.dispatchSetAppValue({ importTracker: pending.tracker });

    if (pending.clearPersistence) {
      localStorage.removeItem('importProgress');
    } else if (pending.persistence) {
      this.persistImportState(pending.persistence);
    }
  };

  queueImportProgressUpdate = (tracker, persistence, clearPersistence = false) => {
    this.pendingImportProgress = {
      tracker,
      persistence,
      clearPersistence
    };

    if (clearPersistence) {
      this.flushPendingImportProgress();
      return;
    }

    if (!this.importProgressUpdateTimer) {
      this.importProgressUpdateTimer = setTimeout(
        this.flushPendingImportProgress,
        IMPORT_PROGRESS_UPDATE_INTERVAL_MS
      );
    }
  };

  clearPendingImportProgress = () => {
    if (this.importProgressUpdateTimer) {
      clearTimeout(this.importProgressUpdateTimer);
      this.importProgressUpdateTimer = null;
    }

    this.pendingImportProgress = null;
  };

  handleImportstageprogress = (body) => {
    // Handle comprehensive import stage progress with counters
    if (body && (body.message || body.stage)) {
      const {
        stage,
        message,
        // Unified counters from backend
        totalAuthorFolders,
        totalBookFolders,
        authorsImported,
        matchedAuthors,
        unmatchedAuthors,
        matchedBooks,
        filesImported,
        currentItemName,
        currentItemType,
        commandId,
        commandStatus
      } = body;

      const previousTracker = this.pendingImportProgress?.tracker || this.props.importTracker || {};
      const previousCounters = previousTracker.counters || {};

      const resolvedAuthorsImported = authorsImported || 0;
      const resolvedMatchedAuthors = matchedAuthors || 0;
      const resolvedUnmatchedAuthors = unmatchedAuthors || 0;

      // Matched and unmatched are mutually exclusive physical author-folder outcomes.
      // AuthorsImported is a useful result counter, but it overlaps matched folders and must
      // never replace or inflate the canonical processed-folder counter.
      const classifiedAuthorFolders = resolvedMatchedAuthors + resolvedUnmatchedAuthors;
      const reportedProcessedAuthors = body.processedAuthorFolders || 0;
      const authorProcessedTotal = Math.max(reportedProcessedAuthors, classifiedAuthorFolders);
      const authorTotal = Math.max(totalAuthorFolders || 0, classifiedAuthorFolders, authorProcessedTotal);

      const nextCounters = {
        processedAuthorFolders: authorProcessedTotal,
        totalAuthorFolders: authorTotal,
        processedBookFolders: body.processedBookFolders || 0,
        totalBookFolders: totalBookFolders || 0,
        authorsImported: resolvedAuthorsImported,
        matchedBooks: matchedBooks || 0,
        filesImported: filesImported || 0
      };

      // Multiple publishers emit partial payloads. Keep the UI stable by merging monotonically.
      const mergedCounters = {
        processedAuthorFolders: Math.max(nextCounters.processedAuthorFolders, previousCounters.processedAuthorFolders || 0),
        totalAuthorFolders: Math.max(nextCounters.totalAuthorFolders, previousCounters.totalAuthorFolders || 0),
        processedBookFolders: Math.max(nextCounters.processedBookFolders, previousCounters.processedBookFolders || 0),
        totalBookFolders: Math.max(nextCounters.totalBookFolders, previousCounters.totalBookFolders || 0),
        authorsImported: Math.max(nextCounters.authorsImported, previousCounters.authorsImported || 0),
        matchedBooks: Math.max(nextCounters.matchedBooks, previousCounters.matchedBooks || 0),
        filesImported: Math.max(nextCounters.filesImported, previousCounters.filesImported || 0)
      };

      // Compute overall progress from the merged counters so the progress bar matches the displayed totals.
      // (Avoid stage-weighted progress which can show 75% at "2/13 authors, 5/75 units".)
      const authorRatio = mergedCounters.totalAuthorFolders > 0
        ? mergedCounters.processedAuthorFolders / mergedCounters.totalAuthorFolders
        : null;

      const unitRatio = mergedCounters.totalBookFolders > 0
        ? mergedCounters.processedBookFolders / mergedCounters.totalBookFolders
        : null;

      // Progress ring should reflect book units processed, not authors.
      // Authors can be highly skewed (1 author with 500 units), which makes author-based progress misleading.
      // Use units when available; fall back to authors only if units denominator is unknown.
      let computedProgress = 0;
      if (unitRatio != null) {
        computedProgress = Math.round(unitRatio * 100);
      } else if (authorRatio != null) {
        computedProgress = Math.round(authorRatio * 100);
      }

      const mergedProgress = Math.max(0, Math.min(100, computedProgress));

      // Remove the duplicate top notification - everything goes in the main progress tracker
      
      // Track command ID/type if provided, otherwise find it from active commands.
      let resolvedCommandId = commandId;
      let resolvedCommandStatus = commandStatus;
      let resolvedOperation = previousTracker.operation || null;

      if (!resolvedCommandId || !resolvedCommandStatus || !resolvedOperation) {
        const { commands } = this.props;
        const activeImportCommand = commands && commands.find(isImportProgressCommand);

        resolvedCommandId = resolvedCommandId || activeImportCommand?.id;
        resolvedCommandStatus = resolvedCommandStatus || activeImportCommand?.status;
        resolvedOperation = activeImportCommand?.name || resolvedOperation;
      }

      const progressTimestamp = Date.now();
      const tracker = {
        isActive: true,
        stage,
        message,
        progress: mergedProgress,
        counters: mergedCounters,
        currentItemName,
        currentItemType,
        currentBookMatched: body.currentBookMatched,
        currentBookType: body.currentBookType,
        commandId: resolvedCommandId || null,
        commandStatus: resolvedCommandStatus || null,
        operation: resolvedOperation,
        lastUpdated: progressTimestamp
      };
      
      const persistence = {
        stage,
        progress: mergedProgress,
        counters: mergedCounters,
        currentItemType,
        currentBookMatched: body.currentBookMatched,
        currentBookType: body.currentBookType,
        commandId: resolvedCommandId || null,
        commandStatus: resolvedCommandStatus || null,
        operation: resolvedOperation,
        timestamp: progressTimestamp
      };
      const isComplete = stage === 'ImportComplete' || stage === 6;

      this.queueImportProgressUpdate(tracker, persistence, isComplete);
    } else if (body) {
      // Check if this is a single author import from pending queue
      const stage = body.stage;
      if (stage === 'ImportingAuthorsToDatabase' && 
          body.currentProgress === 1 && 
          body.totalProgress === 1 &&
          body.currentItemType === 'author') {
        
        // Limit to max 3 concurrent notifications to prevent UI clutter
        if (this.pendingImportNotifications.size >= 3) {
          // Hide the oldest notification
          const oldestId = this.pendingImportNotifications.values().next().value;
          this.props.dispatchHideMessage({ id: oldestId });
          this.pendingImportNotifications.delete(oldestId);
        }
        
        const notificationId = `pending-import-${Date.now()}`;
        this.pendingImportNotifications.add(notificationId);
        
        // Show notification for single author import with 10-second auto-dismiss
        this.props.dispatchShowMessage({
          id: notificationId,
          name: 'PendingAuthorImport',
          message: body.message || `Importing: ${body.currentItemName}`,
          type: 'info',
          hideAfter: 10,  // Auto-dismiss after 10 seconds
          onDismiss: () => {
            // Clean up tracking when notification is dismissed
            this.pendingImportNotifications.delete(notificationId);
          }
        });
        
        // Clean up tracking after auto-dismiss time
        setTimeout(() => {
          this.pendingImportNotifications.delete(notificationId);
        }, 11000);
      }
    }
  };

  handleImportsummary = (body) => {
    if (body) {
      this.clearPendingImportProgress();
      // Clear the legacy sidebar progress message (if present) and the import tracker
      this.props.dispatchHideMessage({ id: 'import-progress-main' });
      this.props.dispatchSetAppValue({
        importTracker: getDefaultImportTracker()
      });
      
      // Clear localStorage
      localStorage.removeItem('importProgress');

      // Create a clean summary message
      const authorsAdded = body.totalAuthorsAdded || 0;
      const booksMatched = body.totalBooksMatched || 0;
      const summaryMessage = `Import Complete! Authors Added: ${authorsAdded}, Books Matched: ${booksMatched}`;

      this.props.dispatchShowMessage({
        id: 'import-summary',
        name: 'ImportSummary',
        message: summaryMessage,
        type: 'success',
        hideAfter: 0,  // Don't auto-hide, user must dismiss
        clickable: true,
        onClick: () => {
          this.props.dispatchHideMessage({ id: 'import-summary' });
        }
      });
    }
  };
  
  //
  // Import State Persistence
  
  persistImportState = (state) => {
    try {
      if (!state || typeof state !== 'object') {
        return;
      }

      // Do not persist human-readable strings that may include filesystem paths.
      // Persist only structured progress state so the chip/drawer can recover after reload.
      const {
        message: _message,
        currentItemName: _currentItemName,
        ...safeState
      } = state;

      localStorage.setItem('importProgress', JSON.stringify(safeState));
    } catch (e) {
      console.error('[SignalR] Failed to persist import state:', e);
    }
  };
  
  restoreImportState = () => {
    try {
      const saved = localStorage.getItem('importProgress');
      if (saved) {
        const parsed = JSON.parse(saved);
        // Check if state is still valid (less than 24 hours old)
        if (parsed.timestamp && Date.now() - parsed.timestamp < 86400000) {
          // Ensure we never rehydrate persisted filesystem paths from older sessions.
          const {
            message: _message,
            currentItemName: _currentItemName,
            ...state
          } = parsed || {};

          try {
            localStorage.setItem('importProgress', JSON.stringify(state));
          } catch {
            // best-effort only
          }

          console.debug('[SignalR] Restoring import state');
          
          // Restore the import tracker (header chip + drawer)
          this.props.dispatchSetAppValue({
            importTracker: {
              isActive: true,
              stage: state.stage,
              message: null,
              progress: state.progress || 0,
              counters: state.counters || {
                processedAuthorFolders: 0,
                totalAuthorFolders: 0,
                processedBookFolders: 0,
                totalBookFolders: 0,
                authorsImported: 0,
                matchedBooks: 0,
                filesImported: 0
              },
              currentItemName: null,
              currentItemType: state.currentItemType,
              currentBookMatched: state.currentBookMatched,
              currentBookType: state.currentBookType,
              commandId: state.commandId || null,
              commandStatus: state.commandStatus || null,
              operation: state.operation || null,
              lastUpdated: state.timestamp || Date.now()
            }
          });
          return true;
        } else {
          // State too old, clear it
          localStorage.removeItem('importProgress');
        }
      }
    } catch (e) {
      console.error('[SignalR] Failed to restore import state:', e);
      localStorage.removeItem('importProgress');
    }
    return false;
  };

  cleanupStaleImportState = () => {
    try {
      const tracker = this.props.importTracker || {};
      if (!tracker.isActive) {
        return;
      }

      const lastUpdated = tracker.lastUpdated;
      if (!lastUpdated) {
        return;
      }

      // If we haven't received progress updates recently and there is no active import command,
      // clear the persisted state so the chip doesn't get "stuck" after a restart or crash.
      const ageMs = Date.now() - lastUpdated;
      const stage = tracker.stage;
      const isCompleteStage = stage === 'ImportComplete' || stage === 6;
      const isComplete = isCompleteStage || (tracker.progress != null && tracker.progress >= 100);
      const staleAfterMs = isComplete ? 5000 : 45000;

      if (ageMs < staleAfterMs) {
        return;
      }

      const { commands } = this.props;
      const activeImportCommand = commands && commands.find(isImportProgressCommand);

      if (!activeImportCommand) {
        this.clearPendingImportProgress();
        localStorage.removeItem('importProgress');
        this.props.dispatchSetAppValue({
          importTracker: getDefaultImportTracker()
        });
      }
    } catch (e) {
      // best-effort only
    }
  };

  //
  // Listeners

  onStartFail = (error) => {
    console.error('[signalR] failed to connect');
    console.error(error);

    this.props.dispatchSetAppValue({
      isConnected: false,
      isReconnecting: false,
      isDisconnected: false,
      isRestarting: false
    });
  };

  onStart = () => {
    console.debug('[signalR] connected');

    this.props.dispatchSetAppValue({
      isConnected: true,
      isReconnecting: false,
      isDisconnected: false,
      isRestarting: false
    });
  };

  onReconnecting = () => {
    this.props.dispatchSetAppValue({ isReconnecting: true });
  };

  onReconnected = () => {

    const {
      dispatchFetchCommands,
      dispatchFetchAuthor,
      dispatchSetAppValue
    } = this.props;

    dispatchSetAppValue({
      isConnected: true,
      isReconnecting: false,
      isDisconnected: false,
      isRestarting: false
    });

    // Check for and clean up any orphaned modals after reconnection
    if (detectOrphanedModals()) {
      console.warn('[signalR] Detected orphaned modals after reconnection, cleaning up');
      forceCloseAllModals();
    }

    // Repopulate the page (if a repopulator is set) to ensure things
    // are in sync after reconnecting.
    dispatchFetchAuthor();
    dispatchFetchCommands();
    repopulatePage();
  };

  onClose = () => {
    console.debug('[signalR] connection closed');
  };

  onReceiveMessage = (message) => {
    console.debug('[signalR] received', message?.name, message?.body?.action);

    this.handleMessage(message);
  };

  //
  // Render

  render() {
    return null;
  }
}

SignalRConnector.propTypes = {
  isReconnecting: PropTypes.bool.isRequired,
  isDisconnected: PropTypes.bool.isRequired,
  isQueuePopulated: PropTypes.bool.isRequired,
  commands: PropTypes.array,
  dispatchFetchCommands: PropTypes.func.isRequired,
  dispatchUpdateCommand: PropTypes.func.isRequired,
  dispatchFinishCommand: PropTypes.func.isRequired,
  dispatchPauseCommand: PropTypes.func.isRequired,
  dispatchResumeCommand: PropTypes.func.isRequired,
  dispatchSetAppValue: PropTypes.func.isRequired,
  dispatchSetVersion: PropTypes.func.isRequired,
  dispatchUpdate: PropTypes.func.isRequired,
  dispatchUpdateItem: PropTypes.func.isRequired,
  dispatchRemoveItem: PropTypes.func.isRequired,
  dispatchDeleteAuthorBooks: PropTypes.func.isRequired,
  dispatchFetchAuthor: PropTypes.func.isRequired,
  dispatchFetchHealth: PropTypes.func.isRequired,
  dispatchFetchQualityDefinitions: PropTypes.func.isRequired,
  dispatchFetchQueue: PropTypes.func.isRequired,
  dispatchFetchQueueDetails: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchFetchTags: PropTypes.func.isRequired,
  dispatchFetchTagDetails: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(SignalRConnector);
