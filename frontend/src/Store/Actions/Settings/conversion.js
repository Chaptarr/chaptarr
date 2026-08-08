import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.conversion';

//
// Actions Types

export const FETCH_CONVERSION_SETTINGS = 'settings/conversion/fetchConversionSettings';
export const SAVE_CONVERSION_SETTINGS = 'settings/conversion/saveConversionSettings';
export const SET_CONVERSION_SETTINGS_VALUE = 'settings/conversion/setConversionSettingsValue';

//
// Action Creators

export const fetchConversionSettings = createThunk(FETCH_CONVERSION_SETTINGS);
export const saveConversionSettings = createThunk(SAVE_CONVERSION_SETTINGS);
export const setConversionSettingsValue = createAction(SET_CONVERSION_SETTINGS_VALUE, (payload) => {
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
    [FETCH_CONVERSION_SETTINGS]: createFetchHandler(section, '/config/conversion'),
    [SAVE_CONVERSION_SETTINGS]: createSaveHandler(section, '/config/conversion')
  },

  //
  // Reducers

  reducers: {
    [SET_CONVERSION_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }
};
