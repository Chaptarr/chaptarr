import $ from 'jquery';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { set } from 'Store/Actions/baseActions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import requestAction from 'Utilities/requestAction';
import getSectionState from 'Utilities/State/getSectionState';
import updateSectionState from 'Utilities/State/updateSectionState';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'oAuth';
const callbackUrl = `${window.location.origin}${window.Chaptarr.urlBase}/oauth.html`;

//
// State

export const defaultState = {
  authorizing: false,
  result: null,
  error: null
};

//
// Actions Types

export const START_OAUTH = 'oAuth/startOAuth';
export const SET_OAUTH_VALUE = 'oAuth/setOAuthValue';
export const RESET_OAUTH = 'oAuth/resetOAuth';

//
// Action Creators

export const startOAuth = createThunk(START_OAUTH);
export const setOAuthValue = createAction(SET_OAUTH_VALUE);
export const resetOAuth = createAction(RESET_OAUTH);

//
// Helpers

function parseOAuthQuery(query) {
  const queryParams = {};
  const trimmedQuery = (query || '').replace(/^\?/, '');

  const params = new URLSearchParams(trimmedQuery);
  params.forEach((value, key) => {
    queryParams[key] = value;
  });

  return queryParams;
}

function createOAuthError(payload, errorMessage) {
  return {
    status: 400,
    responseJSON: [
      {
        propertyName: payload.name,
        errorMessage
      }
    ]
  };
}

function openOAuthWindow(url, payload, windowName) {
  const newWindow = window.open(
    '',
    windowName,
    'popup=yes,width=600,height=700'
  );

  if (
    !newWindow ||
    newWindow.closed ||
    typeof newWindow.closed == 'undefined'
  ) {
    return {
      error: createOAuthError(payload, 'Pop-ups are being blocked by your browser')
    };
  }

  try {
    newWindow.opener = null;
  } catch (e) {
    // Ignore opener isolation errors
  }

  try {
    newWindow.location.href = url;
  } catch (e) {
    newWindow.location = url;
  }

  return { newWindow };
}

function showOAuthWindow(url, payload) {
  const deferred = $.Deferred();
  const windowName = `chaptarr_oauth_${Date.now()}_${Math.random().toString(16).slice(2)}`;
  const storageKey = `chaptarr.oauth.${windowName}`;

  try {
    window.localStorage.removeItem(storageKey);
  } catch (e) {
    // Ignore storage errors
  }

  const {
    newWindow,
    error
  } = openOAuthWindow(url, payload, windowName);

  if (error) {
    return deferred.reject(error).promise();
  }

  const pollIntervalMs = 250;
  const timeoutMs = 5 * 60 * 1000;
  const startTime = Date.now();

  const poll = setInterval(() => {
    let query = null;

    try {
      query = window.localStorage.getItem(storageKey);
    } catch (e) {
      // Ignore storage errors
    }

    if (query) {
      clearInterval(poll);

      try {
        window.localStorage.removeItem(storageKey);
      } catch (e) {
        // Ignore storage errors
      }

      try {
        newWindow.close();
      } catch (e) {
        // Ignore window close errors
      }

      deferred.resolve(parseOAuthQuery(query));
      return;
    }

    if (newWindow.closed) {
      clearInterval(poll);

      deferred.reject(createOAuthError(payload, 'OAuth window was closed before authorization completed'));
      return;
    }

    if (Date.now() - startTime > timeoutMs) {
      clearInterval(poll);
      try {
        newWindow.close();
      } catch (e) {
        // Ignore window close errors
      }

      deferred.reject(createOAuthError(payload, 'OAuth timed out waiting for authorization'));
    }
  }, pollIntervalMs);

  return deferred.promise();
}

function showPollingOAuthWindow(response, payload, requestPayload) {
  const deferred = $.Deferred();
  const windowName = `chaptarr_oauth_${Date.now()}_${Math.random().toString(16).slice(2)}`;
  const {
    newWindow,
    error
  } = openOAuthWindow(response.oauthUrl, payload, windowName);

  if (error) {
    return deferred.reject(error).promise();
  }

  const pollIntervalMs = 1000;
  const closedGraceMs = 10 * 1000;
  const timeoutMs = 5 * 60 * 1000;
  const startTime = Date.now();
  let stopped = false;
  let closedTime = null;

  function closeWindow() {
    try {
      newWindow.close();
    } catch (e) {
      // Ignore window close errors
    }
  }

  function poll() {
    if (stopped) {
      return;
    }

    if (Date.now() - startTime > timeoutMs) {
      stopped = true;
      closeWindow();
      deferred.reject(createOAuthError(payload, 'OAuth timed out waiting for authorization'));
      return;
    }

    requestAction({
      action: 'getOAuthToken',
      queryParams: {
        pinId: response.pinId
      },
      ...requestPayload
    }).done((data) => {
      if (stopped) {
        return;
      }

      if (data && data.authToken) {
        stopped = true;
        closeWindow();
        deferred.resolve(data);
        return;
      }

      if (newWindow.closed) {
        closedTime = closedTime || Date.now();

        if (Date.now() - closedTime > closedGraceMs) {
          stopped = true;
          deferred.reject(createOAuthError(payload, 'OAuth window was closed before authorization completed'));
          return;
        }
      } else {
        closedTime = null;
      }

      setTimeout(poll, pollIntervalMs);
    }).fail((xhr) => {
      stopped = true;
      closeWindow();
      deferred.reject(xhr);
    });
  }

  setTimeout(poll, pollIntervalMs);

  return deferred.promise();
}

function executeIntermediateRequest(payload, ajaxOptions) {
  return createAjaxRequest(ajaxOptions).request.then((data) => {
    return requestAction({
      action: 'continueOAuth',
      queryParams: {
        ...data,
        callbackUrl
      },
      ...payload
    });
  });
}

function getOAuthToken(payload, startResponse, data) {
  return requestAction({
    action: 'getOAuthToken',
    queryParams: {
      ...startResponse,
      ...data
    },
    ...payload
  });
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [START_OAUTH]: function(getState, payload, dispatch) {
    const {
      name,
      section: actionSection,
      ...otherPayload
    } = payload;

    const actionPayload = {
      action: 'startOAuth',
      queryParams: { callbackUrl },
      ...otherPayload
    };

    dispatch(setOAuthValue({
      authorizing: true
    }));

    const promise = requestAction(actionPayload)
      .then((response) => {
        if (response.oauthUrl && response.pinId) {
          return showPollingOAuthWindow(response, payload, otherPayload);
        }

        if (response.oauthUrl) {
          return showOAuthWindow(response.oauthUrl, payload).then((data) => {
            return getOAuthToken(otherPayload, response, data);
          });
        }

        return executeIntermediateRequest(otherPayload, response).then((intermediateResponse) => {
          return showOAuthWindow(intermediateResponse.oauthUrl, payload).then((data) => {
            return getOAuthToken(otherPayload, intermediateResponse, data);
          });
        });
      })
      .then((response) => {
        dispatch(setOAuthValue({
          authorizing: false,
          result: response,
          error: null
        }));
      });

    promise.done(() => {
      // Clear any previously set save error.
      dispatch(set({
        section: actionSection,
        saveError: null
      }));
    });

    promise.fail((xhr) => {
      const actions = [
        setOAuthValue({
          authorizing: false,
          result: null,
          error: xhr
        })
      ];

      dispatch(batchActions(actions));
    });
  }

});

//
// Reducers

export const reducers = createHandleActions({

  [SET_OAUTH_VALUE]: function(state, { payload }) {
    const newState = Object.assign(getSectionState(state, section), payload);

    return updateSectionState(state, section, newState);
  },

  [RESET_OAUTH]: function(state) {
    return updateSectionState(state, section, defaultState);
  }

}, defaultState, section);
