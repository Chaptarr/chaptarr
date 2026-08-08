import { createAction } from 'redux-actions';
import { sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import createFetchHandler from './Creators/createFetchHandler';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';
import createSetSettingValueReducer from './Creators/Reducers/createSetSettingValueReducer';
import createSetTableOptionReducer from './Creators/Reducers/createSetTableOptionReducer';

//
// Variables

export const section = 'series';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  isSaving: false,
  saveError: null,
  sortKey: 'position',
  sortDirection: sortDirections.ASCENDING,
  items: [],

  columns: [
    {
      name: 'monitored',
      columnLabel: 'Monitored',
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'title',
      label: 'Title',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'series',
      label: 'Series',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'position',
      label: 'Number',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'narrator',
      label: 'Narrator',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'releaseDate',
      label: 'Release Date',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'pageCount',
      label: 'Pages',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'duration',
      label: 'Duration',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'rating',
      label: 'Rating',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'status',
      label: 'Status',
      isVisible: true
    },
    {
      name: 'actions',
      columnLabel: 'Actions',
      isVisible: true,
      isModifiable: false
    }
  ]
};

export const persistState = [
  'series.sortKey',
  'series.sortDirection',
  'series.columns',
  'series.tableOptions'
];

//
// Actions Types

export const FETCH_SERIES = 'series/fetchSeries';
export const FETCH_SERIES_BY_ID = 'series/fetchSeriesById';
export const SET_SERIES_SORT = 'books/setSeriesSort';
export const SET_SERIES_TABLE_OPTION = 'books/setSeriesTableOption';
export const CLEAR_SERIES = 'series/clearSeries';
export const SET_SERIES_VALUE = 'books/setBookValue';

//
// Action Creators

export const fetchSeries = createThunk(FETCH_SERIES);
export const fetchSeriesById = createThunk(FETCH_SERIES_BY_ID);
export const setSeriesSort = createAction(SET_SERIES_SORT);
export const setSeriesTableOption = createAction(SET_SERIES_TABLE_OPTION);
export const clearSeries = createAction(CLEAR_SERIES);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_SERIES]: function(getState, payload, dispatch) {
    const { authorId, mediaType: payloadMediaType } = payload;
    const state = getState();
    // Use mediaType from payload if provided, otherwise from state, otherwise default to audiobook
    const mediaType = payloadMediaType || state.authorDetails?.selectedMediaType || 'audiobook';
    const url = authorId ? `/series?authorId=${authorId}&mediaType=${mediaType}` : '/series';
    return createFetchHandler(section, url)(getState, {}, dispatch);
  },

  [FETCH_SERIES_BY_ID]: function(getState, payload, dispatch) {
    return createFetchHandler(section, '/series')(getState, payload, dispatch);
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_SERIES_SORT]: createSetClientSideCollectionSortReducer(section),

  [SET_SERIES_TABLE_OPTION]: createSetTableOptionReducer(section),

  [SET_SERIES_VALUE]: createSetSettingValueReducer(section),

  [CLEAR_SERIES]: (state) => {
    return Object.assign({}, state, {
      isFetching: false,
      isPopulated: false,
      error: null,
      items: []
    });
  }

}, defaultState, section);
