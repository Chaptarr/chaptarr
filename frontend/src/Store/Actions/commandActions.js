import { batchActions } from 'redux-batched-actions';
import { messageTypes } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import { isSameCommand } from 'Utilities/Command';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { hideMessage, showMessage } from './appActions';
import { removeItem, set, update, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createRemoveItemHandler from './Creators/createRemoveItemHandler';
import translate from 'Utilities/String/translate';

//
// Variables

export const section = 'commands';

let lastCommand = null;
let lastCommandTimeout = null;
const removeCommandTimeoutIds = {};
const commandFinishedCallbacks = {};
const pausedCommands = new Set();

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

//
// Helpers

function showCommandMessage(payload, dispatch) {
  const {
    id,
    name,
    trigger,
    message,
    body = {},
    status
  } = payload;

  const {
    sendUpdatesToClient,
    suppressMessages
  } = body;

  if (!message || !body || !sendUpdatesToClient || suppressMessages) {
    return;
  }

  let type = messageTypes.INFO;
  let hideAfter = 0;
  let clickable = false;
  let onClick = undefined;

  // Handle scan commands specially
  if (name === 'RescanFolders' &&
      (status === 'started' || status === 'paused')) {
    // Import/scan progress is shown via the header import tracker (SignalR ImportStageProgress).
    // Avoid a persistent sidebar message that blocks navigation.
    return;
  } else if (status === 'completed') {
    type = messageTypes.SUCCESS;
    hideAfter = 4;
  } else if (status === 'failed') {
    type = messageTypes.ERROR;
    hideAfter = trigger === 'manual' ? 10 : 4;
  }

  dispatch(showMessage({
    id,
    name,
    message,
    type,
    hideAfter,
    clickable,
    onClick
  }));
}

function shouldRestoreCommandMessage(command) {
  const { status } = command;

  return status === 'queued' || status === 'started' || status === 'paused';
}

function scheduleRemoveCommand(command, dispatch) {
  const {
    id,
    status
  } = command;

  const timeoutId = removeCommandTimeoutIds[id];

  if (timeoutId) {
    clearTimeout(timeoutId);
    delete removeCommandTimeoutIds[id];
  }

  if (status === 'queued' || status === 'paused') {
    // Don't auto-remove queued or paused commands
    return;
  }

  removeCommandTimeoutIds[id] = setTimeout(() => {
    dispatch(batchActions([
      removeCommand({ section: 'commands', id }),
      hideMessage({ id })
    ]));

    delete removeCommandTimeoutIds[id];
  }, 60000 * 5);
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
  });
}

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_COMMANDS]: function(getState, payload, dispatch) {
    dispatch(set({ section: 'commands', isFetching: true }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/command',
      data: payload,
      traditional: true
    });

    request.done((data) => {
      dispatch(batchActions([
        update({ section: 'commands', data }),
        set({
          section: 'commands',
          isFetching: false,
          isPopulated: true,
          error: null
        })
      ]));

      data
        .filter(shouldRestoreCommandMessage)
        .forEach((command) => {
          showCommandMessage(command, dispatch);
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

  [CANCEL_COMMAND]: createRemoveItemHandler(section, '/command'),
  
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
    dispatch(updateItem({ section: 'commands', ...payload }));
  },

  [UPDATE_COMMAND]: function(getState, payload, dispatch) {
    // If command status changed to paused, add to pausedCommands
    if (payload.status === 'paused') {
      pausedCommands.add(payload.id);
    }
    // If command resumed (started), remove from pausedCommands
    else if (payload.status === 'started') {
      pausedCommands.delete(payload.id);
    }
    // If command completed or failed, remove from paused set
    else if (payload.status === 'completed' || payload.status === 'failed') {
      pausedCommands.delete(payload.id);
    }
    
    dispatch(updateItem({ section: 'commands', ...payload }));

    showCommandMessage(payload, dispatch);
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

    const commandFinished = commandFinishedCallbacks[payload.id];

    if (commandFinished) {
      commandFinished(payload);
    }

    delete commandFinishedCallbacks[payload.id];
    pausedCommands.delete(payload.id);

    dispatch(updateItem({ section: 'commands', ...payload }));
    scheduleRemoveCommand(payload, dispatch);
    showCommandMessage(payload, dispatch);
  },

  [REMOVE_COMMAND]: function(getState, payload, dispatch) {
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
