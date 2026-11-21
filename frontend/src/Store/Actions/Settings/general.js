import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.general';
const saveSettings = createSaveHandler(section, '/config/host');

//
// Actions Types

export const FETCH_GENERAL_SETTINGS = 'settings/general/fetchGeneralSettings';
export const SET_GENERAL_SETTINGS_VALUE = 'settings/general/setGeneralSettingsValue';
export const SAVE_GENERAL_SETTINGS = 'settings/general/saveGeneralSettings';
export const SAVE_GENERAL_SETTINGS_FIELDS = 'settings/general/saveGeneralSettingsFields';

//
// Action Creators

export const fetchGeneralSettings = createThunk(FETCH_GENERAL_SETTINGS);
export const saveGeneralSettings = createThunk(SAVE_GENERAL_SETTINGS);
export const saveGeneralSettingsFields = createThunk(SAVE_GENERAL_SETTINGS_FIELDS);
export const setGeneralSettingsValue = createAction(SET_GENERAL_SETTINGS_VALUE, (payload) => {
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
    item: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_GENERAL_SETTINGS]: createFetchHandler(section, '/config/host'),
    [SAVE_GENERAL_SETTINGS]: saveSettings,
    [SAVE_GENERAL_SETTINGS_FIELDS]: (getState, payload, dispatch) => saveSettings(getState, payload, dispatch, true)
  },

  //
  // Reducers

  reducers: {
    [SET_GENERAL_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
