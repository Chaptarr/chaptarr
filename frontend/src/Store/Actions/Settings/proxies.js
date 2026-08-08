import { createAction } from 'redux-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import { set } from '../baseActions';
import createFetchHandler from '../Creators/createFetchHandler';
import createHandleActions from '../Creators/createHandleActions';
import createRemoveItemHandler from '../Creators/createRemoveItemHandler';
import createSaveProviderHandler from '../Creators/createSaveProviderHandler';
import createSetSettingValueReducer from '../Creators/Reducers/createSetSettingValueReducer';

//
// Variables

const section = 'settings.proxies';

//
// Actions Types

export const FETCH_PROXIES = 'settings/proxies/fetchProxies';
export const SAVE_PROXY = 'settings/proxies/saveProxy';
export const DELETE_PROXY = 'settings/proxies/deleteProxy';
export const TEST_PROXY = 'settings/proxies/testProxy';
export const SET_PROXY_VALUE = 'settings/proxies/setProxyValue';

//
// Action Creators

export const fetchProxies = createThunk(FETCH_PROXIES);
export const saveProxy = createThunk(SAVE_PROXY);
export const deleteProxy = createThunk(DELETE_PROXY);
export const testProxy = createThunk(TEST_PROXY);

export const setProxyValue = createAction(SET_PROXY_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Details

export default {

  //
  // State

  defaultState: {
    isFetching: false,
    error: null,
    isDeleting: false,
    deleteError: null,
    items: [],
    pendingChanges: {},
    isSaving: false,
    saveError: null,
    isTesting: false,
    testError: null
  },

  //
  // Action Handlers

  actionHandlers: handleThunks({
    [FETCH_PROXIES]: createFetchHandler(section, '/settings/proxy'),
    [SAVE_PROXY]: createSaveProviderHandler(section, '/settings/proxy'),
    [DELETE_PROXY]: createRemoveItemHandler(section, '/settings/proxy'),
    [TEST_PROXY]: (getState, payload, dispatch) => {
      dispatch(set({ section, isTesting: true }));

      const promise = fetch('/api/v1/settings/proxy/test', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Api-Key': window.Chaptarr.apiKey
        },
        body: JSON.stringify(payload)
      });

      promise.done((data) => {
        dispatch(set({
          section,
          isTesting: false,
          testError: data.isValid ? null : data.message
        }));
      });

      promise.fail((xhr) => {
        dispatch(set({
          section,
          isTesting: false,
          testError: xhr
        }));
      });
    }
  }),

  //
  // Reducers

  reducers: {
    [SET_PROXY_VALUE]: createSetSettingValueReducer(section)
  }

};