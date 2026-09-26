import { createAction } from 'redux-actions';
import { clearBooks } from 'Store/Actions/bookActions';
import { invalidateQuery } from 'Store/Actions/bookInfiniteScrollActions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.mediaManagement';

//
// Actions Types

export const FETCH_MEDIA_MANAGEMENT_SETTINGS = 'settings/mediaManagement/fetchMediaManagementSettings';
export const SAVE_MEDIA_MANAGEMENT_SETTINGS = 'settings/mediaManagement/saveMediaManagementSettings';
export const SET_MEDIA_MANAGEMENT_SETTINGS_VALUE = 'settings/mediaManagement/setMediaManagementSettingsValue';
export const INVALIDATE_BOOK_COVER_SCOPE_RESOURCES = 'settings/mediaManagement/invalidateBookCoverScopeResources';

//
// Action Creators

export const fetchMediaManagementSettings = createThunk(FETCH_MEDIA_MANAGEMENT_SETTINGS);
export const saveMediaManagementSettings = createThunk(SAVE_MEDIA_MANAGEMENT_SETTINGS);
export const invalidateBookCoverScopeResources = createThunk(INVALIDATE_BOOK_COVER_SCOPE_RESOURCES);
export const setMediaManagementSettingsValue = createAction(SET_MEDIA_MANAGEMENT_SETTINGS_VALUE, (payload) => {
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
    [FETCH_MEDIA_MANAGEMENT_SETTINGS]: createFetchHandler(section, '/config/mediamanagement'),
    [SAVE_MEDIA_MANAGEMENT_SETTINGS]: createSaveHandler(section, '/config/mediamanagement'),
    [INVALIDATE_BOOK_COVER_SCOPE_RESOURCES]: (getState, payload, dispatch) => {
      dispatch(clearBooks());

      Object.keys(getState().bookInfiniteScroll?.queries || {}).forEach((queryKey) => {
        dispatch(invalidateQuery(queryKey));
      });
    }
  },

  //
  // Reducers

  reducers: {
    [SET_MEDIA_MANAGEMENT_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
