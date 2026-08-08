import { createAction } from 'redux-actions';
import createBulkRemoveItemHandler from 'Store/Actions/Creators/createBulkRemoveItemHandler';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createRemoveItemHandler from 'Store/Actions/Creators/createRemoveItemHandler';
import createSaveProviderHandler from 'Store/Actions/Creators/createSaveProviderHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.importListExclusions';

//
// Actions Types

export const FETCH_IMPORT_LIST_EXCLUSIONS = 'settings/importListExclusions/fetchImportListExclusions';
export const SAVE_IMPORT_LIST_EXCLUSION = 'settings/importListExclusions/saveImportListExclusion';
export const DELETE_IMPORT_LIST_EXCLUSION = 'settings/importListExclusions/deleteImportListExclusion';
export const BULK_DELETE_IMPORT_LIST_EXCLUSIONS = 'settings/importListExclusions/bulkDeleteImportListExclusions';
export const SET_IMPORT_LIST_EXCLUSION_VALUE = 'settings/importListExclusions/setImportListExclusionValue';

//
// Action Creators

export const fetchImportListExclusions = createThunk(FETCH_IMPORT_LIST_EXCLUSIONS);
export const saveImportListExclusion = createThunk(SAVE_IMPORT_LIST_EXCLUSION);
export const deleteImportListExclusion = createThunk(DELETE_IMPORT_LIST_EXCLUSION);
export const bulkDeleteImportListExclusions = createThunk(BULK_DELETE_IMPORT_LIST_EXCLUSIONS);

export const setImportListExclusionValue = createAction(SET_IMPORT_LIST_EXCLUSION_VALUE, (payload) => {
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
    items: [],
    isSaving: false,
    saveError: null,
    isDeleting: false,
    deleteError: null,
    pendingChanges: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_IMPORT_LIST_EXCLUSIONS]: createFetchHandler(section, '/importlistexclusion'),
    [SAVE_IMPORT_LIST_EXCLUSION]: createSaveProviderHandler(section, '/importlistexclusion'),
    [DELETE_IMPORT_LIST_EXCLUSION]: createRemoveItemHandler(section, '/importlistexclusion'),
    [BULK_DELETE_IMPORT_LIST_EXCLUSIONS]: createBulkRemoveItemHandler(section, '/importlistexclusion/bulk')
  },

  //
  // Reducers

  reducers: {
    [SET_IMPORT_LIST_EXCLUSION_VALUE]: createSetSettingValueReducer(section)
  }

};
