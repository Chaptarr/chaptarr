import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set, update } from './baseActions';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'convertPreview';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: []
};

//
// Actions Types

export const FETCH_CONVERT_PREVIEW = 'convertPreview/fetchConvertPreview';
export const CLEAR_CONVERT_PREVIEW = 'convertPreview/clearConvertPreview';

//
// Action Creators

export const fetchConvertPreview = createThunk(FETCH_CONVERT_PREVIEW);
export const clearConvertPreview = createAction(CLEAR_CONVERT_PREVIEW);

//
// Action Handlers

// The preview slice is shared by every convert window. Only the latest request may write to it, so a
// slow response for a window that was closed (or replaced by another book's) is discarded.
let latestPreviewRequest = 0;

export const actionHandlers = handleThunks({

  [FETCH_CONVERT_PREVIEW]: function(getState, payload, dispatch) {
    const requestId = ++latestPreviewRequest;

    dispatch(batchActions([
      update({ section, data: [] }),
      set({ section, isFetching: true, isPopulated: false, error: null })
    ]));

    const { request, abortRequest } = createAjaxRequest({
      url: '/convert',
      data: payload,
      traditional: true
    });

    request.done((data) => {
      if (requestId !== latestPreviewRequest) {
        return;
      }

      dispatch(batchActions([
        update({ section, data }),
        set({ section, isFetching: false, isPopulated: true, error: null })
      ]));
    });

    request.fail((xhr) => {
      if (requestId !== latestPreviewRequest) {
        return;
      }

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });

    return () => {
      // Closing the window supersedes its request even if the abort arrives after the response.
      if (requestId === latestPreviewRequest) {
        latestPreviewRequest++;
      }

      abortRequest();
    };
  }

});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_CONVERT_PREVIEW]: (state) => {
    return Object.assign({}, state, defaultState);
  }

}, defaultState, section);
