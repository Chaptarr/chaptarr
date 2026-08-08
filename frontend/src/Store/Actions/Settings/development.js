import { batchActions } from 'redux-batched-actions';
import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import { set, update } from '../baseActions';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.development';

function createTestDevelopmentSettingsHandler() {
  return function(getState, payload, dispatch) {
    dispatch(set({ section, isTesting: true, successMessages: [] }));

    const state = getSectionState(getState(), section, true);
    const testData = Object.assign({}, state.item, state.pendingChanges, payload);

    const { request } = createAjaxRequest({
      url: '/config/development/test',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify(testData)
    });

    request.done((data) => {
      dispatch(set({
        section,
        isTesting: false,
        saveError: null,
        successMessages: (data && data.successMessages) ? data.successMessages : []
      }));
    });

    request.fail((xhr) => {
      dispatch(set({
        section,
        isTesting: false,
        saveError: xhr.aborted ? null : xhr,
        successMessages: []
      }));
    });
  };
}

function createSaveDevelopmentSettingsHandler() {
  return function(getState, payload, dispatch) {
    dispatch(set({ section, isSaving: true, successMessages: [] }));

    const state = getSectionState(getState(), section, true);
    const shouldTestMetadataServerUrl = Object.prototype.hasOwnProperty.call(state.pendingChanges || {}, 'metadataServerUrl');
    const saveData = Object.assign({}, state.item, state.pendingChanges, payload);

    const promise = createAjaxRequest({
      url: '/config/development',
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify(saveData)
    }).request;

    promise.done((data) => {
      dispatch(batchActions([
        update({ section, data }),

        set({
          section,
          isSaving: false,
          saveError: null,
          pendingChanges: {}
        })
      ]));

      if (shouldTestMetadataServerUrl && saveData.metadataServerUrl && String(saveData.metadataServerUrl).trim().length) {
        dispatch(testDevelopmentSettings());
      }
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isSaving: false,
        saveError: xhr,
        successMessages: []
      }));
    });

    return promise;
  };
}

//
// Actions Types

export const FETCH_DEVELOPMENT_SETTINGS = 'settings/development/fetchDevelopmentSettings';
export const SET_DEVELOPMENT_SETTINGS_VALUE = 'settings/development/setDevelopmentSettingsValue';
export const SAVE_DEVELOPMENT_SETTINGS = 'settings/development/saveDevelopmentSettings';
export const TEST_DEVELOPMENT_SETTINGS = 'settings/development/testDevelopmentSettings';

//
// Action Creators

export const fetchDevelopmentSettings = createThunk(FETCH_DEVELOPMENT_SETTINGS);
export const saveDevelopmentSettings = createThunk(SAVE_DEVELOPMENT_SETTINGS);
export const testDevelopmentSettings = createThunk(TEST_DEVELOPMENT_SETTINGS);
export const setDevelopmentSettingsValue = createAction(SET_DEVELOPMENT_SETTINGS_VALUE, (payload) => {
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
    isPopulated: false,
    error: null,
    pendingChanges: {},
    isSaving: false,
    saveError: null,
    isTesting: false,
    successMessages: [],
    item: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_DEVELOPMENT_SETTINGS]: createFetchHandler(section, '/config/development'),
    [SAVE_DEVELOPMENT_SETTINGS]: createSaveDevelopmentSettingsHandler(),
    [TEST_DEVELOPMENT_SETTINGS]: createTestDevelopmentSettingsHandler()
  },

  //
  // Reducers

  reducers: {
    [SET_DEVELOPMENT_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
