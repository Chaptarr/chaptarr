import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.hardcoverConfig';

//
// Actions Types

export const FETCH_HARDCOVER_CONFIG = 'settings/hardcoverConfig/fetchHardcoverConfig';

//
// Action Creators

export const fetchHardcoverConfig = createThunk(FETCH_HARDCOVER_CONFIG);

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
    [FETCH_HARDCOVER_CONFIG]: createFetchHandler(section, '/config/hardcover')
  },

  //
  // Reducers

  reducers: {}

};

