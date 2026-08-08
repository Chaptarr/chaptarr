import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import serverSideCollectionHandlers from 'Utilities/serverSideCollectionHandlers';
import translate from 'Utilities/String/translate';
import { set, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createRemoveItemHandler from './Creators/createRemoveItemHandler';
import createServerSideCollectionHandlers from './Creators/createServerSideCollectionHandlers';
import createClearReducer from './Creators/Reducers/createClearReducer';
import createSetTableOptionReducer from './Creators/Reducers/createSetTableOptionReducer';

//
// Variables

export const section = 'ignored';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  pageSize: 20,
  sortKey: 'date',
  sortDirection: sortDirections.DESCENDING,
  error: null,
  items: [],
  isRemoving: false,

  columns: [
    {
      name: 'sourceTitle',
      label: () => translate('SourceTitle'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'downloadId',
      label: () => translate('DownloadId'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'downloadClientName',
      label: () => translate('DownloadClient'),
      isSortable: false,
      isVisible: true
    },
    {
      name: 'date',
      label: () => translate('Date'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'isInClient',
      label: () => translate('InClient'),
      isSortable: false,
      isVisible: true
    },
    {
      name: 'actions',
      columnLabel: () => translate('Actions'),
      isVisible: true,
      isModifiable: false
    }
  ]
};

export const persistState = [
  'ignored.pageSize',
  'ignored.sortKey',
  'ignored.sortDirection',
  'ignored.columns'
];

//
// Action Types

export const FETCH_IGNORED = 'ignored/fetchIgnored';
export const GOTO_FIRST_IGNORED_PAGE = 'ignored/gotoIgnoredFirstPage';
export const GOTO_PREVIOUS_IGNORED_PAGE = 'ignored/gotoIgnoredPreviousPage';
export const GOTO_NEXT_IGNORED_PAGE = 'ignored/gotoIgnoredNextPage';
export const GOTO_LAST_IGNORED_PAGE = 'ignored/gotoIgnoredLastPage';
export const GOTO_IGNORED_PAGE = 'ignored/gotoIgnoredPage';
export const SET_IGNORED_SORT = 'ignored/setIgnoredSort';
export const SET_IGNORED_TABLE_OPTION = 'ignored/setIgnoredTableOption';
export const REMOVE_IGNORED_ITEM = 'ignored/removeIgnoredItem';
export const REMOVE_IGNORED_ITEMS = 'ignored/removeIgnoredItems';
export const CLEAR_IGNORED = 'ignored/clearIgnored';

//
// Action Creators

export const fetchIgnored = createThunk(FETCH_IGNORED);
export const gotoIgnoredFirstPage = createThunk(GOTO_FIRST_IGNORED_PAGE);
export const gotoIgnoredPreviousPage = createThunk(GOTO_PREVIOUS_IGNORED_PAGE);
export const gotoIgnoredNextPage = createThunk(GOTO_NEXT_IGNORED_PAGE);
export const gotoIgnoredLastPage = createThunk(GOTO_LAST_IGNORED_PAGE);
export const gotoIgnoredPage = createThunk(GOTO_IGNORED_PAGE);
export const setIgnoredSort = createThunk(SET_IGNORED_SORT);
export const setIgnoredTableOption = createAction(SET_IGNORED_TABLE_OPTION);
export const removeIgnoredItem = createThunk(REMOVE_IGNORED_ITEM);
export const removeIgnoredItems = createThunk(REMOVE_IGNORED_ITEMS);
export const clearIgnored = createAction(CLEAR_IGNORED);

//
// Action Handlers

export const actionHandlers = handleThunks({
  ...createServerSideCollectionHandlers(
    section,
    '/ignored',
    fetchIgnored,
    {
      [serverSideCollectionHandlers.FETCH]: FETCH_IGNORED,
      [serverSideCollectionHandlers.FIRST_PAGE]: GOTO_FIRST_IGNORED_PAGE,
      [serverSideCollectionHandlers.PREVIOUS_PAGE]: GOTO_PREVIOUS_IGNORED_PAGE,
      [serverSideCollectionHandlers.NEXT_PAGE]: GOTO_NEXT_IGNORED_PAGE,
      [serverSideCollectionHandlers.LAST_PAGE]: GOTO_LAST_IGNORED_PAGE,
      [serverSideCollectionHandlers.EXACT_PAGE]: GOTO_IGNORED_PAGE,
      [serverSideCollectionHandlers.SORT]: SET_IGNORED_SORT
    }),

  [REMOVE_IGNORED_ITEM]: createRemoveItemHandler(section, '/ignored'),

  [REMOVE_IGNORED_ITEMS]: function(getState, payload, dispatch) {
    const {
      ids
    } = payload;

    dispatch(batchActions([
      ...ids.map((id) => {
        return updateItem({
          section,
          id,
          isRemoving: true
        });
      }),

      set({ section, isRemoving: true })
    ]));

    const promise = createAjaxRequest({
      url: '/ignored/bulk',
      method: 'DELETE',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ids })
    }).request;

    promise.done(() => {
      dispatch(fetchIgnored());
      dispatch(set({ section, isRemoving: false }));
    });

    promise.fail(() => {
      dispatch(batchActions([
        ...ids.map((id) => {
          return updateItem({
            section,
            id,
            isRemoving: false
          });
        }),

        set({ section, isRemoving: false })
      ]));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_IGNORED_TABLE_OPTION]: createSetTableOptionReducer(section),

  [CLEAR_IGNORED]: createClearReducer(section, {
    isFetching: false,
    isPopulated: false,
    error: null,
    items: [],
    totalPages: 0,
    totalRecords: 0
  })

}, defaultState, section);
