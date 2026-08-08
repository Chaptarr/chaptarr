import { createAction } from 'redux-actions';
import { sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import { set } from './baseActions';
import { filterPredicates, sortPredicates } from './bookActions';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';

//
// Variables

export const section = 'authorDetails';

//
// State

export const defaultState = {
  sortKey: 'releaseDate',
  sortDirection: sortDirections.DESCENDING,
  secondarySortKey: 'releaseDate',
  secondarySortDirection: sortDirections.DESCENDING,

  selectedFilterKey: 'authorId',
  filterValue: '',

  sortPredicates: {
    ...sortPredicates
  },

  filters: [
    {
      key: 'authorId',
      label: 'Author',
      filters: [
        {
          key: 'authorId',
          value: 0
        }
      ]
    }
  ],

  filterPredicates

};

export const persistState = [
  'authorDetails.sortKey',
  'authorDetails.sortDirection'
];

//
// Actions Types

export const SET_AUTHOR_DETAILS_SORT = 'authorIndex/setAuthorDetailsSort';
export const SET_AUTHOR_DETAILS_ID = 'authorIndex/setAuthorDetailsId';
export const SET_AUTHOR_DETAILS_FILTER_VALUE = 'authorIndex/setAuthorDetailsFilterValue';

//
// Action Creators

export const setAuthorDetailsSort = createAction(SET_AUTHOR_DETAILS_SORT);
export const setAuthorDetailsId = createThunk(SET_AUTHOR_DETAILS_ID);
export const setAuthorDetailsFilterValue = createAction(SET_AUTHOR_DETAILS_FILTER_VALUE);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [SET_AUTHOR_DETAILS_ID]: function(getState, payload, dispatch) {
    const {
      authorId
    } = payload;

    dispatch(set({
      section,
      filters: [
        {
          key: 'authorId',
          label: 'Author',
          filters: [
            {
              key: 'authorId',
              value: authorId
            }
          ]
        }
      ]
    }));
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_AUTHOR_DETAILS_SORT]: createSetClientSideCollectionSortReducer(section),
  
  [SET_AUTHOR_DETAILS_FILTER_VALUE]: (state, { payload }) => {
    return Object.assign({}, state, { filterValue: payload });
  }

}, defaultState, section);
