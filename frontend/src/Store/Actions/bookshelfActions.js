import { createAction } from 'redux-actions';
import { filterBuilderTypes, filterBuilderValueTypes, filterTypePredicates, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import { isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { SET_SELECTED_MEDIA_TYPE } from './appActions';
import { filterPredicates, filters } from './authorActions';
import { set } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionFilterReducer from './Creators/Reducers/createSetClientSideCollectionFilterReducer';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';

//
// Variables

export const section = 'bookshelf';

//
// State

export const defaultState = {
  isSaving: false,
  saveError: null,
  sortKey: 'sortName',
  sortDirection: sortDirections.ASCENDING,
  secondarySortKey: 'sortName',
  secondarySortDirection: sortDirections.ASCENDING,
  selectedMediaType: localStorage.getItem('selectedMediaType') || 'audiobook',
  selectedFilterKey: 'all',
  filters,
  filterPredicates: {
    ...filterPredicates,

    monitored: function(item, filterValue, type, state) {
      const predicate = filterTypePredicates[type];
      const monitored = isAuthorMonitoredForSelection(item, state.selectedMediaType);

      return predicate(monitored, filterValue);
    }
  },

  filterBuilderProps: [
    {
      name: 'monitored',
      label: () => translate('Monitored'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.BOOL
    },
    {
      name: 'status',
      label: () => translate('Status'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.AUTHOR_STATUS
    },
    {
      name: 'qualityProfileId',
      label: () => translate('QualityProfile'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.QUALITY_PROFILE
    },
    {
      name: 'metadataProfileId',
      label: () => translate('MetadataProfile'),
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.METADATA_PROFILE
    },
    {
      name: 'rootFolderPath',
      label: () => translate('RootFolderPath'),
      type: filterBuilderTypes.EXACT
    },
    {
      name: 'tags',
      label: () => translate('Tags'),
      type: filterBuilderTypes.ARRAY,
      valueType: filterBuilderValueTypes.TAG
    }
  ]
};

export const persistState = [
  'bookshelf.sortKey',
  'bookshelf.sortDirection',
  'bookshelf.selectedFilterKey',
  'bookshelf.customFilters'
];

//
// Actions Types

export const SET_BOOKSHELF_SORT = 'bookshelf/setBookshelfSort';
export const SET_BOOKSHELF_FILTER = 'bookshelf/setBookshelfFilter';
export const SAVE_BOOKSHELF = 'bookshelf/saveBookshelf';

//
// Action Creators

export const setBookshelfSort = createAction(SET_BOOKSHELF_SORT);
export const setBookshelfFilter = createAction(SET_BOOKSHELF_FILTER);
export const saveBookshelf = createThunk(SAVE_BOOKSHELF);

//
// Action Handlers

export const actionHandlers = handleThunks({

  [SAVE_BOOKSHELF]: function(getState, payload, dispatch) {
    const {
      authorIds,
      monitor,
      mediaType
    } = payload;

    const data = {
      authors: authorIds.map((id) => ({ id }))
    };

    if (monitor != null) {
      data.monitoringOptions = { monitor, mediaType };
    }

    dispatch(set({
      section,
      isSaving: true
    }));

    const promise = createAjaxRequest({
      url: '/bookshelf',
      method: 'POST',
      data: JSON.stringify(data),
      dataType: 'json'
    }).request;

    promise.done(() => {
      dispatch(set({
        section,
        isSaving: false,
        saveError: null
      }));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isSaving: false,
        saveError: xhr
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_BOOKSHELF_SORT]: createSetClientSideCollectionSortReducer(section),
  [SET_BOOKSHELF_FILTER]: createSetClientSideCollectionFilterReducer(section),

  [SET_SELECTED_MEDIA_TYPE]: function(state, { payload }) {
    return {
      ...state,
      selectedMediaType: payload.mediaType
    };
  }

}, defaultState, section);
