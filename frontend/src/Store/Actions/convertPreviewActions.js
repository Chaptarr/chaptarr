import { createAction } from 'redux-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createFetchHandler from './Creators/createFetchHandler';
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

export const actionHandlers = handleThunks({

  [FETCH_CONVERT_PREVIEW]: createFetchHandler('convertPreview', '/convert')

});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_CONVERT_PREVIEW]: (state) => {
    return Object.assign({}, state, defaultState);
  }

}, defaultState, section);
