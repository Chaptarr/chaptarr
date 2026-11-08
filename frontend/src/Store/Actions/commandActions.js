import { batchActions } from 'redux-batched-actions';
import { messageTypes } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import { isCommandComplete, isCommandFailed, isSameCommand } from 'Utilities/Command';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import { hideMessage, showMessage } from './appActions';
import { removeItem, set, update, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createRemoveItemHandler from './Creators/createRemoveItemHandler';
import { reconcileAuthorSearchCommand } from './releaseActions';

//
// Variables

export const section = 'commands';

let lastCommand = null;
let lastCommandTimeout = null;
const removeCommandTimeoutIds = {};
const commandFinishedCallbacks = {};

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: [],
  handlers: {}
};

//
// Actions Types

export const FETCH_COMMANDS = 'commands/fetchCommands';
export const EXECUTE_COMMAND = 'commands/executeCommand';
export const CANCEL_COMMAND = 'commands/cancelCommand';
export const PAUSE_COMMAND = 'commands/pauseCommand';
export const RESUME_COMMAND = 'commands/resumeCommand';
export const PAUSE_ALL_COMMANDS = 'commands/pauseAllCommands';
export const CANCEL_ALL_COMMANDS = 'commands/cancelAllCommands';
export const RESUME_ALL_COMMANDS = 'commands/resumeAllCommands';
export const ADD_COMMAND = 'commands/addCommand';
export const UPDATE_COMMAND = 'commands/updateCommand';
export const FINISH_COMMAND = 'commands/finishCommand';
export const REMOVE_COMMAND = 'commands/removeCommand';

//
// Action Creators

export const fetchCommands = createThunk(FETCH_COMMANDS);
export const executeCommand = createThunk(EXECUTE_COMMAND);
export const cancelCommand = createThunk(CANCEL_COMMAND);
export const pauseCommand = createThunk(PAUSE_COMMAND);
export const resumeCommand = createThunk(RESUME_COMMAND);
export const pauseAllCommands = createThunk(PAUSE_ALL_COMMANDS);
export const cancelAllCommands = createThunk(CANCEL_ALL_COMMANDS);
export const resumeAllCommands = createThunk(RESUME_ALL_COMMANDS);
export const addCommand = createThunk(ADD_COMMAND);
export const updateCommand = createThunk(UPDATE_COMMAND);
export const finishCommand = createThunk(FINISH_COMMAND);
export const removeCommand = createThunk(REMOVE_COMMAND);

export function showCommandProgress(payload) {
  return (dispatch, getState) => {
    const command = getState().commands.items.find((item) => item.id === payload.commandId);

    if (!isCommandComplete(command) && !isCommandFailed(command)) {
      dispatch(showMessage({ ...payload, hideAfter: 0 }));
    }
  };
}

//
// Helpers

function completeCommandCallback(command) {
  const commandFinished = commandFinishedCallbacks[command.id];
  delete commandFinishedCallbacks[command.id];
  if (commandFinished) {
    commandFinished(command);
  }
}

function clearCommandProgress(getState, dispatch, shouldClear) {
  (getState().app.messages?.items || []).forEach((message) => {
    if (message.commandId != null && message.hideAfter === 0 && shouldClear(message.commandId)) {
      dispatch(hideMessage({ id: message.id }));
    }
  });
}

function showCommandMessage(payload, dispatch, getState) {
  const {
    id,
    name,
    trigger,
    message: commandMessage,
    body = {},
    status
  } = payload;

  const {
    sendUpdatesToClient,
    suppressMessages
  } = body;

  const isAuthorSearch = name === 'InteractiveAuthorSearch';
  const isTerminal = isCommandComplete(payload) || isCommandFailed(payload);
  let message = commandMessage;
  if (isAuthorSearch) {
    const format = translate(body.mediaType === 'ebook' ? 'Ebooks' : 'Audiobooks');
    const author = body.authorName || body.authorId;
    if (status === 'queued') {
      message = translate('AuthorSearchQueued');
    } else if (isTerminal) {
      message = translate('AuthorSearchViewResults', { author, format });
    } else {
      message = translate('AuthorSearchProgress', { author, format, done: body.booksDone || 0, total: body.booksTotal || 0 });
    }
  }

  if (!message || !sendUpdatesToClient || suppressMessages) {
    return;
  }

  let type = messageTypes.INFO;
  let hideAfter = 0;
  const link = name === 'InteractiveAuthorSearch' && body.authorId && body.mediaType ?
    `/author/${body.authorId}?tab=search&mediaType=${encodeURIComponent(body.mediaType)}` : undefined;
  if (link) {
    getState().app.messages.items.forEach((item) => {
      if (item.id !== id && item.link === link) {
        dispatch(hideMessage({ id: item.id }));
      }
    });
  }
  const clickable = !!link;
  const onClick = isAuthorSearch && isTerminal ? () => dispatch(hideMessage({ id })) : undefined;

  // Handle scan commands specially
  if (name === 'RescanFolders' &&
      (status === 'started' || status === 'paused')) {
    // Import/scan progress is shown via the header import tracker (SignalR ImportStageProgress).
    // Avoid a persistent sidebar message that blocks navigation.
    return;
  } else if (status === 'completed') {
    type = messageTypes.SUCCESS;
    hideAfter = 4;
  } else if (isCommandFailed(payload)) {
    type = messageTypes.ERROR;
    hideAfter = trigger === 'manual' ? 10 : 4;
  }

  if (isAuthorSearch && isTerminal && body.resultsExpireAt) {
    hideAfter = Math.max(0, (Date.parse(body.resultsExpireAt) - Date.now()) / 1000);
    if (!hideAfter) {
      return;
    }
  }

  dispatch(showMessage({
    id,
    commandId: id,
    name,
    message,
    type,
    hideAfter,
    clickable,
    link,
    onClick
  }));
}

function shouldRestoreCommandMessage(command) {
  const { status } = command;

  return status === 'queued' || status === 'started' || status === 'paused';
}

function scheduleRemoveCommand(command, dispatch) {
  const { id } = command;

  const timeoutId = removeCommandTimeoutIds[id];

  if (timeoutId) {
    clearTimeout(timeoutId);
    delete removeCommandTimeoutIds[id];
  }

  if (shouldRestoreCommandMessage(command)) {
    return;
  }

  const removeAfter = command.name === 'InteractiveAuthorSearch' && command.body?.resultsExpireAt ?
    Math.max(0, Date.parse(command.body.resultsExpireAt) - Date.now()) : 60000 * 5;
  removeCommandTimeoutIds[id] = setTimeout(() => {
    dispatch(batchActions([
      removeCommand({ section: 'commands', id }),
      hideMessage({ id })
    ]));

    delete removeCommandTimeoutIds[id];
  }, removeAfter);
}

export function executeCommandHelper(payload, dispatch) {
  // TODO: show a message for the user
  if (lastCommand && isSameCommand(lastCommand, payload)) {
    console.warn('Please wait at least 5 seconds before running this command again');
  }

  lastCommand = payload;

  // clear last command after 5 seconds.
  if (lastCommandTimeout) {
    clearTimeout(lastCommandTimeout);
  }

  lastCommandTimeout = setTimeout(() => {
    lastCommand = null;
  }, 5000);

  const {
    commandFinished,
    ...requestPayload
  } = payload;

  const promise = createAjaxRequest({
    url: '/command',
    method: 'POST',
    data: JSON.stringify(requestPayload),
    dataType: 'json'
  }).request;

  return promise.then((data) => {
    if (commandFinished) {
      commandFinishedCallbacks[data.id] = commandFinished;
    }

    dispatch(addCommand(data));
    return data;
  });
}

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_COMMANDS]: function(getState, payload, dispatch) {
    const previousCommands = new Map(getState().commands.items.map((command) => [command.id, command]));
    dispatch(set({ section: 'commands', isFetching: true }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/command',
      data: payload,
      traditional: true
    });

    request.done((data) => {
      const commands = new Map(data.map((command) => [command.id, command]));

      // Events received during the request must not be undone by its older snapshot.
      getState().commands.items.forEach((command) => {
        const incoming = commands.get(command.id);
        if (!isCommandComplete(incoming) && !isCommandFailed(incoming) &&
            (command !== previousCommands.get(command.id) ||
             (incoming && (isCommandComplete(command) || isCommandFailed(command))))) {
          commands.set(command.id, command);
        }
      });

      data = Array.from(commands.values());
      dispatch(batchActions([
        update({ section: 'commands', data }),
        set({
          section: 'commands',
          isFetching: false,
          isPopulated: true,
          error: null
        })
      ]));

      dispatch(reconcileAuthorSearchCommand({ commands: data }));

      const activeIds = new Set(data.filter(shouldRestoreCommandMessage).map((command) => command.id));
      clearCommandProgress(getState, dispatch, (id) => !activeIds.has(id));

      data
        .filter(shouldRestoreCommandMessage)
        .forEach((command) => {
          showCommandMessage(command, dispatch, getState);
        });
    });

    request.fail((xhr) => {
      dispatch(set({
        section: 'commands',
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });

    return abortRequest;
  },

  [EXECUTE_COMMAND]: function(getState, payload, dispatch) {
    return executeCommandHelper(payload, dispatch);
  },

  [CANCEL_COMMAND]: function(getState, payload, dispatch) {
    const request = createRemoveItemHandler(section, '/command')(getState, payload, dispatch);
    request.fail((xhr) => {
      if (!xhr.aborted) {
        dispatch(showMessage({
          id: `command-cancel-${payload.id}`,
          name: 'CancelCommand',
          message: getErrorMessage(xhr, translate('UnableToCancelTask')),
          type: messageTypes.ERROR,
          hideAfter: 8
        }));
      }
    });
    return request;
  },

  [PAUSE_COMMAND]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: `/command/${payload.id}/pause`,
      method: 'POST'
    }).request;

    promise.always(() => {
      dispatch(fetchCommands());
    });
  },

  [RESUME_COMMAND]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: `/command/${payload.id}/resume`,
      method: 'POST'
    }).request;

    promise.always(() => {
      dispatch(fetchCommands());
    });
  },

  [PAUSE_ALL_COMMANDS]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: '/command/pause/all',
      method: 'POST'
    }).request;

    promise.done((data) => {
      dispatch(showMessage({
        id: 'bulk-commands-pause',
        name: 'PauseAllCommands',
        message: translate('PausedCountCommands', { count: data }),
        type: messageTypes.SUCCESS,
        hideAfter: 5
      }));
    });

    promise.fail((xhr) => {
      dispatch(showMessage({
        id: 'bulk-commands-pause',
        name: 'PauseAllCommands',
        message: xhr?.responseJSON?.message || xhr?.responseText || 'Unable to pause commands',
        type: messageTypes.ERROR,
        hideAfter: 8
      }));
    });

    promise.always(() => {
      dispatch(fetchCommands());
    });
  },

  [CANCEL_ALL_COMMANDS]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: '/command/cancel/all',
      method: 'POST'
    }).request;

    promise.done((data) => {
      dispatch(showMessage({
        id: 'bulk-commands-cancel',
        name: 'CancelAllCommands',
        message: translate('CancelledCountCommands', { count: data }),
        type: messageTypes.SUCCESS,
        hideAfter: 5
      }));
    });

    promise.fail((xhr) => {
      dispatch(showMessage({
        id: 'bulk-commands-cancel',
        name: 'CancelAllCommands',
        message: xhr?.responseJSON?.message || xhr?.responseText || 'Unable to cancel commands',
        type: messageTypes.ERROR,
        hideAfter: 8
      }));
    });

    promise.always(() => {
      dispatch(fetchCommands());
    });
  },

  [RESUME_ALL_COMMANDS]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: '/command/resume/all',
      method: 'POST'
    }).request;

    promise.done((data) => {
      dispatch(showMessage({
        id: 'bulk-commands-resume',
        name: 'ResumeAllCommands',
        message: translate('ResumedCountCommands', { count: data }),
        type: messageTypes.SUCCESS,
        hideAfter: 5
      }));
    });

    promise.fail((xhr) => {
      dispatch(showMessage({
        id: 'bulk-commands-resume',
        name: 'ResumeAllCommands',
        message: xhr?.responseJSON?.message || xhr?.responseText || 'Unable to resume commands',
        type: messageTypes.ERROR,
        hideAfter: 8
      }));
    });

    promise.always(() => {
      dispatch(fetchCommands());
    });
  },

  [ADD_COMMAND]: function(getState, payload, dispatch) {
    const current = getState().commands.items.find((command) => command.id === payload.id);
    if (isCommandComplete(current) || isCommandFailed(current)) {
      completeCommandCallback(current);
      if (shouldRestoreCommandMessage(payload)) {
        return;
      }
    }

    dispatch(updateItem({ section: 'commands', ...payload }));
    dispatch(reconcileAuthorSearchCommand({ command: payload }));
    if (isCommandComplete(payload) || isCommandFailed(payload)) {
      completeCommandCallback(payload);
    }
  },

  [UPDATE_COMMAND]: function(getState, payload, dispatch) {
    const current = getState().commands.items.find((command) => command.id === payload.id);
    if (shouldRestoreCommandMessage(payload) && (isCommandComplete(current) || isCommandFailed(current))) {
      return;
    }

    dispatch(updateItem({ section: 'commands', ...payload }));
    dispatch(reconcileAuthorSearchCommand({ command: payload }));

    showCommandMessage(payload, dispatch, getState);
    scheduleRemoveCommand(payload, dispatch);
  },

  [FINISH_COMMAND]: function(getState, payload, dispatch) {
    const state = getState();
    const handlers = state.commands.handlers;

    Object.keys(handlers).forEach((key) => {
      const handler = handlers[key];

      if (handler.name === payload.name) {
        dispatch(handler.handler(payload));
      }
    });

    completeCommandCallback(payload);

    dispatch(updateItem({ section: 'commands', ...payload }));
    dispatch(reconcileAuthorSearchCommand({ command: payload }));
    clearCommandProgress(getState, dispatch, (id) => id === payload.id);
    scheduleRemoveCommand(payload, dispatch);
    showCommandMessage(payload, dispatch, getState);
  },

  [REMOVE_COMMAND]: function(getState, payload, dispatch) {
    clearCommandProgress(getState, dispatch, (id) => id === payload.id);
    dispatch(removeItem({ section: 'commands', ...payload }));
  }

});

//
// Reducers

export const reducers = createHandleActions({
  [UPDATE_COMMAND]: (state, { payload }) => {
    const newState = Object.assign({}, state);
    const commands = state.items;
    const index = commands.findIndex((c) => c.id === payload.id);

    newState.items = [...commands];

    if (index >= 0) {
      const updatedCommand = Object.assign({}, commands[index], payload);
      newState.items[index] = updatedCommand;
    }

    return newState;
  }
}, defaultState, section);
