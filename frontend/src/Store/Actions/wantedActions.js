import $ from 'jquery';
import { createAction } from 'redux-actions';
import { filterTypes, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import serverSideCollectionHandlers from 'Utilities/serverSideCollectionHandlers';
import translate from 'Utilities/String/translate';
import { set } from './baseActions';
import createBatchToggleBookMonitoredHandler from './Creators/createBatchToggleBookMonitoredHandler';
import createHandleActions from './Creators/createHandleActions';
import createServerSideCollectionHandlers from './Creators/createServerSideCollectionHandlers';
import createClearReducer from './Creators/Reducers/createClearReducer';
import createSetTableOptionReducer from './Creators/Reducers/createSetTableOptionReducer';

//
// Variables

export const section = 'wanted';

//
// State

export const defaultState = {
  authors: {
    isFetching: false,
    isPopulated: false,
    pageSize: 20,
    sortKey: 'id',
    sortDirection: sortDirections.DESCENDING,
    error: null,
    saveError: null,
    pendingAction: null,
    items: [],
    columns: [
      { name: 'authorName', label: () => translate('AuthorName'), isSortable: true, isVisible: true },
      { name: 'providerId', label: () => translate('ProviderId'), isSortable: true, isVisible: true },
      { name: 'createdAt', label: () => translate('RequestedAt'), isSortable: true, isVisible: true },
      { name: 'overallStatus', label: () => translate('Status'), isVisible: true },
      { name: 'actions', columnLabel: () => translate('Actions'), isVisible: true, isModifiable: false }
    ]
  },

  missing: {
    isFetching: false,
    isPopulated: false,
    pageSize: 20,
    sortKey: 'releaseDate',
    sortDirection: sortDirections.DESCENDING,
    error: null,
    items: [],
    selectedMediaType: 'all',

    columns: [
      {
        name: 'authors.sortName',
        label: 'Author',
        isSortable: true,
        isVisible: true
      },
      {
        name: 'books.title',
        label: 'Book',
        isSortable: true,
        isVisible: true
      },
      {
        name: 'mediaType',
        label: 'Type',
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
        name: 'books.lastSearchTime',
        label: 'Last Searched',
        isSortable: true,
        isVisible: false
      },
      {
        name: 'actions',
        columnLabel: 'Actions',
        isVisible: true,
        isModifiable: false
      }
    ],

    selectedFilterKey: 'monitored',

    filters: [
      {
        key: 'monitored',
        label: 'Monitored',
        filters: [
          {
            key: 'monitored',
            value: true,
            type: filterTypes.EQUAL
          }
        ]
      },
      {
        key: 'unmonitored',
        label: 'Unmonitored',
        filters: [
          {
            key: 'monitored',
            value: false,
            type: filterTypes.EQUAL
          }
        ]
      }
    ]
  },

  cutoffUnmet: {
    isFetching: false,
    isPopulated: false,
    pageSize: 20,
    sortKey: 'releaseDate',
    sortDirection: sortDirections.DESCENDING,
    items: [],
    selectedMediaType: 'all',

    columns: [
      {
        name: 'authors.sortName',
        label: 'Author',
        isSortable: true,
        isVisible: true
      },
      {
        name: 'books.title',
        label: 'Book',
        isSortable: true,
        isVisible: true
      },
      {
        name: 'mediaType',
        label: 'Type',
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
        name: 'books.lastSearchTime',
        label: 'Last Searched',
        isSortable: true,
        isVisible: false
      },
      {
        name: 'actions',
        columnLabel: 'Actions',
        isVisible: true,
        isModifiable: false
      }
    ],

    selectedFilterKey: 'monitored',

    filters: [
      {
        key: 'monitored',
        label: 'Monitored',
        filters: [
          {
            key: 'monitored',
            value: true,
            type: filterTypes.EQUAL
          }
        ]
      },
      {
        key: 'unmonitored',
        label: 'Unmonitored',
        filters: [
          {
            key: 'monitored',
            value: false,
            type: filterTypes.EQUAL
          }
        ]
      }
    ]
  }
};

export const persistState = [
  'wanted.authors.pageSize',
  'wanted.authors.sortKey',
  'wanted.authors.sortDirection',
  'wanted.authors.columns',
  'wanted.missing.pageSize',
  'wanted.missing.sortKey',
  'wanted.missing.sortDirection',
  'wanted.missing.selectedFilterKey',
  'wanted.missing.selectedMediaType',
  'wanted.missing.columns',
  'wanted.cutoffUnmet.pageSize',
  'wanted.cutoffUnmet.sortKey',
  'wanted.cutoffUnmet.sortDirection',
  'wanted.cutoffUnmet.selectedFilterKey',
  'wanted.cutoffUnmet.selectedMediaType',
  'wanted.cutoffUnmet.columns'
];

//
// Actions Types

export const FETCH_WANTED_AUTHORS = 'wanted/authors/fetch';
export const FIRST_WANTED_AUTHORS_PAGE = 'wanted/authors/firstPage';
export const PREVIOUS_WANTED_AUTHORS_PAGE = 'wanted/authors/previousPage';
export const NEXT_WANTED_AUTHORS_PAGE = 'wanted/authors/nextPage';
export const LAST_WANTED_AUTHORS_PAGE = 'wanted/authors/lastPage';
export const GOTO_WANTED_AUTHORS_PAGE = 'wanted/authors/page';
export const SET_WANTED_AUTHORS_SORT = 'wanted/authors/sort';
export const SET_WANTED_AUTHORS_TABLE_OPTION = 'wanted/authors/tableOption';
export const CLEAR_WANTED_AUTHORS = 'wanted/authors/clear';
export const RETRY_WANTED_AUTHOR = 'wanted/authors/retry';
export const CANCEL_WANTED_AUTHOR = 'wanted/authors/cancel';
export const DELETE_WANTED_AUTHOR = 'wanted/authors/delete';

export const FETCH_MISSING = 'wanted/missing/fetchMissing';
export const GOTO_FIRST_MISSING_PAGE = 'wanted/missing/gotoMissingFirstPage';
export const GOTO_PREVIOUS_MISSING_PAGE = 'wanted/missing/gotoMissingPreviousPage';
export const GOTO_NEXT_MISSING_PAGE = 'wanted/missing/gotoMissingNextPage';
export const GOTO_LAST_MISSING_PAGE = 'wanted/missing/gotoMissingLastPage';
export const GOTO_MISSING_PAGE = 'wanted/missing/gotoMissingPage';
export const SET_MISSING_SORT = 'wanted/missing/setMissingSort';
export const SET_MISSING_FILTER = 'wanted/missing/setMissingFilter';
export const SET_MISSING_TABLE_OPTION = 'wanted/missing/setMissingTableOption';
export const SET_MISSING_MEDIA_TYPE = 'wanted/missing/setMissingMediaType';
export const CLEAR_MISSING = 'wanted/missing/clearMissing';

export const BATCH_TOGGLE_MISSING_BOOKS = 'wanted/missing/batchToggleMissingBooks';

export const FETCH_CUTOFF_UNMET = 'wanted/cutoffUnmet/fetchCutoffUnmet';
export const GOTO_FIRST_CUTOFF_UNMET_PAGE = 'wanted/cutoffUnmet/gotoCutoffUnmetFirstPage';
export const GOTO_PREVIOUS_CUTOFF_UNMET_PAGE = 'wanted/cutoffUnmet/gotoCutoffUnmetPreviousPage';
export const GOTO_NEXT_CUTOFF_UNMET_PAGE = 'wanted/cutoffUnmet/gotoCutoffUnmetNextPage';
export const GOTO_LAST_CUTOFF_UNMET_PAGE = 'wanted/cutoffUnmet/gotoCutoffUnmetFastPage';
export const GOTO_CUTOFF_UNMET_PAGE = 'wanted/cutoffUnmet/gotoCutoffUnmetPage';
export const SET_CUTOFF_UNMET_SORT = 'wanted/cutoffUnmet/setCutoffUnmetSort';
export const SET_CUTOFF_UNMET_FILTER = 'wanted/cutoffUnmet/setCutoffUnmetFilter';
export const SET_CUTOFF_UNMET_TABLE_OPTION = 'wanted/cutoffUnmet/setCutoffUnmetTableOption';
export const SET_CUTOFF_UNMET_MEDIA_TYPE = 'wanted/cutoffUnmet/setCutoffUnmetMediaType';
export const CLEAR_CUTOFF_UNMET = 'wanted/cutoffUnmet/clearCutoffUnmet';

export const BATCH_TOGGLE_CUTOFF_UNMET_BOOKS = 'wanted/cutoffUnmet/batchToggleCutoffUnmetBooks';

//
// Action Creators

export const fetchWantedAuthors = createThunk(FETCH_WANTED_AUTHORS);
export const gotoWantedAuthorsFirstPage = createThunk(FIRST_WANTED_AUTHORS_PAGE);
export const gotoWantedAuthorsPreviousPage = createThunk(PREVIOUS_WANTED_AUTHORS_PAGE);
export const gotoWantedAuthorsNextPage = createThunk(NEXT_WANTED_AUTHORS_PAGE);
export const gotoWantedAuthorsLastPage = createThunk(LAST_WANTED_AUTHORS_PAGE);
export const gotoWantedAuthorsPage = createThunk(GOTO_WANTED_AUTHORS_PAGE);
export const setWantedAuthorsSort = createThunk(SET_WANTED_AUTHORS_SORT);
export const setWantedAuthorsTableOption = createAction(SET_WANTED_AUTHORS_TABLE_OPTION);
export const clearWantedAuthors = createAction(CLEAR_WANTED_AUTHORS);
export const retryWantedAuthor = createThunk(RETRY_WANTED_AUTHOR);
export const cancelWantedAuthor = createThunk(CANCEL_WANTED_AUTHOR);
export const deleteWantedAuthor = createThunk(DELETE_WANTED_AUTHOR);

export const fetchMissing = createThunk(FETCH_MISSING);
export const gotoMissingFirstPage = createThunk(GOTO_FIRST_MISSING_PAGE);
export const gotoMissingPreviousPage = createThunk(GOTO_PREVIOUS_MISSING_PAGE);
export const gotoMissingNextPage = createThunk(GOTO_NEXT_MISSING_PAGE);
export const gotoMissingLastPage = createThunk(GOTO_LAST_MISSING_PAGE);
export const gotoMissingPage = createThunk(GOTO_MISSING_PAGE);
export const setMissingSort = createThunk(SET_MISSING_SORT);
export const setMissingFilter = createThunk(SET_MISSING_FILTER);
export const setMissingTableOption = createAction(SET_MISSING_TABLE_OPTION);
export const setMissingMediaType = createThunk(SET_MISSING_MEDIA_TYPE);
export const clearMissing = createAction(CLEAR_MISSING);

export const batchToggleMissingBooks = createThunk(BATCH_TOGGLE_MISSING_BOOKS);

export const fetchCutoffUnmet = createThunk(FETCH_CUTOFF_UNMET);
export const gotoCutoffUnmetFirstPage = createThunk(GOTO_FIRST_CUTOFF_UNMET_PAGE);
export const gotoCutoffUnmetPreviousPage = createThunk(GOTO_PREVIOUS_CUTOFF_UNMET_PAGE);
export const gotoCutoffUnmetNextPage = createThunk(GOTO_NEXT_CUTOFF_UNMET_PAGE);
export const gotoCutoffUnmetLastPage = createThunk(GOTO_LAST_CUTOFF_UNMET_PAGE);
export const gotoCutoffUnmetPage = createThunk(GOTO_CUTOFF_UNMET_PAGE);
export const setCutoffUnmetSort = createThunk(SET_CUTOFF_UNMET_SORT);
export const setCutoffUnmetFilter = createThunk(SET_CUTOFF_UNMET_FILTER);
export const setCutoffUnmetTableOption = createAction(SET_CUTOFF_UNMET_TABLE_OPTION);
export const setCutoffUnmetMediaType = createThunk(SET_CUTOFF_UNMET_MEDIA_TYPE);
export const clearCutoffUnmet = createAction(CLEAR_CUTOFF_UNMET);

export const batchToggleCutoffUnmetBooks = createThunk(BATCH_TOGGLE_CUTOFF_UNMET_BOOKS);

//
// Action Handlers

function createPendingAuthorAction(name, method, suffix = '') {
  return (getState, { id }, dispatch) => {
    const actionSection = 'wanted.authors';
    if (getState().wanted.authors.pendingAction) {
      return undefined;
    }
    dispatch(set({ section: actionSection, pendingAction: { id, name }, saveError: null }));
    const finish = () => dispatch(set({ section: actionSection, pendingAction: null }));
    try {
      const request = createAjaxRequest({
        url: `/pendingauthorimport/${id}${suffix}`,
        method
      }).request;
      request.always(finish);
      request.done(() => {
        if (getState().wanted.authors.isPopulated) {
          dispatch(fetchWantedAuthors());
        }
      });
      request.fail((saveError) => dispatch(set({ section: actionSection, saveError })));
      return request;
    } catch (saveError) {
      finish();
      dispatch(set({ section: actionSection, saveError }));
      return $.Deferred().reject(saveError).promise();
    }
  };
}

export const actionHandlers = handleThunks({

  ...createServerSideCollectionHandlers(
    'wanted.authors',
    '/pendingauthorimport/paged',
    fetchWantedAuthors,
    {
      [serverSideCollectionHandlers.FETCH]: FETCH_WANTED_AUTHORS,
      [serverSideCollectionHandlers.FIRST_PAGE]: FIRST_WANTED_AUTHORS_PAGE,
      [serverSideCollectionHandlers.PREVIOUS_PAGE]: PREVIOUS_WANTED_AUTHORS_PAGE,
      [serverSideCollectionHandlers.NEXT_PAGE]: NEXT_WANTED_AUTHORS_PAGE,
      [serverSideCollectionHandlers.LAST_PAGE]: LAST_WANTED_AUTHORS_PAGE,
      [serverSideCollectionHandlers.EXACT_PAGE]: GOTO_WANTED_AUTHORS_PAGE,
      [serverSideCollectionHandlers.SORT]: SET_WANTED_AUTHORS_SORT
    }
  ),

  [RETRY_WANTED_AUTHOR]: createPendingAuthorAction('retry', 'POST', '/retry'),
  [CANCEL_WANTED_AUTHOR]: createPendingAuthorAction('cancel', 'DELETE'),
  [DELETE_WANTED_AUTHOR]: createPendingAuthorAction('delete', 'DELETE', '?delete=true'),

  ...createServerSideCollectionHandlers(
    'wanted.missing',
    '/wanted/missing',
    fetchMissing,
    {
      [serverSideCollectionHandlers.FETCH]: FETCH_MISSING,
      [serverSideCollectionHandlers.FIRST_PAGE]: GOTO_FIRST_MISSING_PAGE,
      [serverSideCollectionHandlers.PREVIOUS_PAGE]: GOTO_PREVIOUS_MISSING_PAGE,
      [serverSideCollectionHandlers.NEXT_PAGE]: GOTO_NEXT_MISSING_PAGE,
      [serverSideCollectionHandlers.LAST_PAGE]: GOTO_LAST_MISSING_PAGE,
      [serverSideCollectionHandlers.EXACT_PAGE]: GOTO_MISSING_PAGE,
      [serverSideCollectionHandlers.SORT]: SET_MISSING_SORT,
      [serverSideCollectionHandlers.FILTER]: SET_MISSING_FILTER
    },
    (getState, payload, data) => {
      // Include author data for missing books
      data.includeAuthor = true;

      const state = getState();
      const selectedMediaType = (state?.wanted?.missing?.selectedMediaType || 'all').toLowerCase();
      if (selectedMediaType !== 'all') {
        data.mediaType = selectedMediaType;
      }
    }
  ),

  [SET_MISSING_MEDIA_TYPE]: function(getState, payload, dispatch) {
    const selectedMediaType = (payload?.mediaType || 'all').toLowerCase();
    dispatch(set({ section: 'wanted.missing', selectedMediaType }));
    dispatch(gotoMissingFirstPage());
  },

  [BATCH_TOGGLE_MISSING_BOOKS]: createBatchToggleBookMonitoredHandler('wanted.missing', fetchMissing),

  ...createServerSideCollectionHandlers(
    'wanted.cutoffUnmet',
    '/wanted/cutoff',
    fetchCutoffUnmet,
    {
      [serverSideCollectionHandlers.FETCH]: FETCH_CUTOFF_UNMET,
      [serverSideCollectionHandlers.FIRST_PAGE]: GOTO_FIRST_CUTOFF_UNMET_PAGE,
      [serverSideCollectionHandlers.PREVIOUS_PAGE]: GOTO_PREVIOUS_CUTOFF_UNMET_PAGE,
      [serverSideCollectionHandlers.NEXT_PAGE]: GOTO_NEXT_CUTOFF_UNMET_PAGE,
      [serverSideCollectionHandlers.LAST_PAGE]: GOTO_LAST_CUTOFF_UNMET_PAGE,
      [serverSideCollectionHandlers.EXACT_PAGE]: GOTO_CUTOFF_UNMET_PAGE,
      [serverSideCollectionHandlers.SORT]: SET_CUTOFF_UNMET_SORT,
      [serverSideCollectionHandlers.FILTER]: SET_CUTOFF_UNMET_FILTER
    },
    (getState, payload, data) => {
      // Include author data for cutoff unmet books
      data.includeAuthor = true;

      const state = getState();
      const selectedMediaType = (state?.wanted?.cutoffUnmet?.selectedMediaType || 'all').toLowerCase();
      if (selectedMediaType !== 'all') {
        data.mediaType = selectedMediaType;
      }
    }
  ),

  [SET_CUTOFF_UNMET_MEDIA_TYPE]: function(getState, payload, dispatch) {
    const selectedMediaType = (payload?.mediaType || 'all').toLowerCase();
    dispatch(set({ section: 'wanted.cutoffUnmet', selectedMediaType }));
    dispatch(gotoCutoffUnmetFirstPage());
  },

  [BATCH_TOGGLE_CUTOFF_UNMET_BOOKS]: createBatchToggleBookMonitoredHandler('wanted.cutoffUnmet', fetchCutoffUnmet)

});

//
// Reducers

export const reducers = createHandleActions({

  [SET_WANTED_AUTHORS_TABLE_OPTION]: createSetTableOptionReducer('wanted.authors'),
  [CLEAR_WANTED_AUTHORS]: createClearReducer('wanted.authors', {
    isFetching: false,
    isPopulated: false,
    error: null,
    saveError: null,
    items: [],
    totalPages: 0,
    totalRecords: 0
  }),

  [SET_MISSING_TABLE_OPTION]: createSetTableOptionReducer('wanted.missing'),
  [SET_CUTOFF_UNMET_TABLE_OPTION]: createSetTableOptionReducer('wanted.cutoffUnmet'),

  [CLEAR_MISSING]: createClearReducer(
    'wanted.missing',
    {
      isFetching: false,
      isPopulated: false,
      error: null,
      items: [],
      totalPages: 0,
      totalRecords: 0
    }
  ),

  [CLEAR_CUTOFF_UNMET]: createClearReducer(
    'wanted.cutoffUnmet',
    {
      isFetching: false,
      isPopulated: false,
      error: null,
      items: [],
      totalPages: 0,
      totalRecords: 0
    }
  )

}, defaultState, section);
