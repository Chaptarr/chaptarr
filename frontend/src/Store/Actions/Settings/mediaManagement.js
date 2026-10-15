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
const setSettingValue = createSetSettingValueReducer(section);
const saveSettings = createSaveHandler(section, '/config/mediamanagement');

//
// Actions Types

export const FETCH_MEDIA_MANAGEMENT_SETTINGS = 'settings/mediaManagement/fetchMediaManagementSettings';
export const SAVE_MEDIA_MANAGEMENT_SETTINGS = 'settings/mediaManagement/saveMediaManagementSettings';
export const SAVE_MEDIA_MANAGEMENT_SETTINGS_FIELDS = 'settings/mediaManagement/saveMediaManagementSettingsFields';
export const SET_MEDIA_MANAGEMENT_SETTINGS_VALUE = 'settings/mediaManagement/setMediaManagementSettingsValue';
export const INVALIDATE_BOOK_COVER_SCOPE_RESOURCES = 'settings/mediaManagement/invalidateBookCoverScopeResources';

//
// Action Creators

export const fetchMediaManagementSettings = createThunk(FETCH_MEDIA_MANAGEMENT_SETTINGS);
export const saveMediaManagementSettings = createThunk(SAVE_MEDIA_MANAGEMENT_SETTINGS);
export const saveMediaManagementSettingsFields = createThunk(SAVE_MEDIA_MANAGEMENT_SETTINGS_FIELDS);
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
    [SAVE_MEDIA_MANAGEMENT_SETTINGS]: saveSettings,
    [SAVE_MEDIA_MANAGEMENT_SETTINGS_FIELDS]: (getState, payload, dispatch) => saveSettings(getState, payload, dispatch, true),
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
    [SET_MEDIA_MANAGEMENT_SETTINGS_VALUE]: (state, action) => {
      const { name, value } = action.payload;
      const current = state.mediaManagement;
      const currentMode = current.pendingChanges.bookMatchingStrictness ?? current.item.bookMatchingStrictness;

      if (name === 'usePathAsTagsFallback' && currentMode === 'strict' && value) {
        return state;
      }

      let next = setSettingValue(state, action);
      if (name !== 'bookMatchingStrictness' || value === currentMode) {
        return next;
      }

      const enteringStrict = value === 'strict';
      const previousFallback = enteringStrict ?
        current.pendingChanges.usePathAsTagsFallback ?? current.item.usePathAsTagsFallback :
        current.pathAsTagsFallbackBeforeStrict;

      // Undo only the temporary checkbox change, never a saved Strict preference.
      if (enteringStrict || (current.pendingChanges.bookMatchingStrictness === 'strict' && previousFallback != null)) {
        next = setSettingValue(next, { payload: { section, name: 'usePathAsTagsFallback', value: enteringStrict ? false : previousFallback } });
      }

      return {
        ...next,
        mediaManagement: { ...next.mediaManagement, pathAsTagsFallbackBeforeStrict: enteringStrict ? previousFallback : undefined }
      };
    }
  }

};
