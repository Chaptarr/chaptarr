import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { filterBuilderTypes, filterBuilderValueTypes, filterTypePredicates, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import sortByName from 'Utilities/Array/sortByName';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { filterPredicates, filters, sortPredicates } from './authorActions';
import { set, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionFilterReducer from './Creators/Reducers/createSetClientSideCollectionFilterReducer';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';
import createSetTableOptionReducer from './Creators/Reducers/createSetTableOptionReducer';

//
// Variables

export const section = 'authorIndex';

//
// State

export const defaultState = {
  isSaving: false,
  saveError: null,
  saveWarning: null,
  isDeleting: false,
  deleteError: null,
  sortKey: 'sortNameLastFirst',
  sortDirection: sortDirections.ASCENDING,
  secondarySortKey: 'sortNameLastFirst',
  secondarySortDirection: sortDirections.ASCENDING,
  view: 'posters',

  posterOptions: {
    detailedProgressBar: false,
    size: 'large',
    showTitle: 'lastFirst',
    showMonitored: true,
    showQualityProfile: true,
    showSearchAction: false
  },

  overviewOptions: {
    showTitle: 'lastFirst',
    detailedProgressBar: false,
    size: 'medium',
    showMonitored: true,
    showQualityProfile: true,
    showLastBook: false,
    showAdded: false,
    showBookCount: true,
    showPath: false,
    showSizeOnDisk: false,
    showSearchAction: false
  },

  tableOptions: {
    showTitle: 'lastFirst',
    showBanners: false,
    showSearchAction: false
  },

  columns: [
    {
      name: 'select',
      columnLabel: () => translate('Select'),
      isSortable: false,
      isVisible: true,
      isModifiable: false,
      isHidden: true
    },
    {
      name: 'status',
      columnLabel: () => translate('Status'),
      isSortable: true,
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'sortName',
      label: () => translate('AuthorName'),
      isSortable: true,
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'qualityProfileId',
      label: () => translate('QualityProfile'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'metadataProfileId',
      label: () => translate('MetadataProfile'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'nextBook',
      label: () => translate('NextBook'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'lastBook',
      label: () => translate('LastBook'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'added',
      label: () => translate('Added'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'bookProgress',
      label: () => translate('Books'),
      isSortable: true,
      isVisible: true
    },
    {
      name: 'path',
      label: () => translate('Path'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'sizeOnDisk',
      label: () => translate('SizeOnDisk'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'genres',
      label: () => translate('Genres'),
      isSortable: false,
      isVisible: false
    },
    {
      name: 'ratings',
      label: () => translate('Rating'),
      isSortable: true,
      isVisible: false
    },
    {
      name: 'tags',
      label: () => translate('Tags'),
      isSortable: false,
      isVisible: false
    },
    {
      name: 'actions',
      columnLabel: () => translate('Actions'),
      isVisible: true,
      isModifiable: false
    }
  ],

  sortPredicates: {
    ...sortPredicates,

    bookProgress: function(item) {
      const { statistics = {} } = item;

      const {
        bookCount = 0,
        bookFileCount
      } = statistics;

      const progress = bookCount ? bookFileCount / bookCount * 100 : 100;

      return progress + bookCount / 1000000;
    },

    nextBook: function(item) {
      if (item.nextBook) {
        return item.nextBook.releaseDate;
      }
      return '1/1/1000';
    },

    lastBook: function(item) {
      if (item.lastBook) {
        return item.lastBook.releaseDate;
      }
      return '1/1/1000';
    },

    bookCount: function(item) {
      const { statistics = {} } = item;

      return statistics.bookCount || 0;
    },

    ratings: function(item) {
      const { ratings = {} } = item;

      return ratings.value;
    }
  },

  selectedFilterKey: 'all',

  filters,

  filterPredicates: {
    ...filterPredicates,

    bookProgress: function(item, filterValue, type) {
      const { statistics = {} } = item;

      const {
        bookCount = 0,
        bookFileCount
      } = statistics;

      const progress = bookCount ?
        bookFileCount / bookCount * 100 :
        100;

      const predicate = filterTypePredicates[type];

      return predicate(progress, filterValue);
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
      name: 'nextBook',
      label: () => translate('NextBook'),
      type: filterBuilderTypes.DATE,
      valueType: filterBuilderValueTypes.DATE
    },
    {
      name: 'lastBook',
      label: () => translate('LastBook'),
      type: filterBuilderTypes.DATE,
      valueType: filterBuilderValueTypes.DATE
    },
    {
      name: 'added',
      label: () => translate('Added'),
      type: filterBuilderTypes.DATE,
      valueType: filterBuilderValueTypes.DATE
    },
    {
      name: 'bookCount',
      label: () => translate('BookCount'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'bookProgress',
      label: () => translate('BookProgress'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'path',
      label: () => translate('Path'),
      type: filterBuilderTypes.STRING
    },
    {
      name: 'sizeOnDisk',
      label: () => translate('SizeOnDisk'),
      type: filterBuilderTypes.NUMBER,
      valueType: filterBuilderValueTypes.BYTES
    },
    {
      name: 'genres',
      label: () => translate('Genres'),
      type: filterBuilderTypes.ARRAY,
      optionsSelector: function(items) {
        const tagList = items.reduce((acc, author) => {
          author.genres.forEach((genre) => {
            acc.push({
              id: genre,
              name: genre
            });
          });

          return acc;
        }, []);

        return tagList.sort(sortByName);
      }
    },
    {
      name: 'ratings',
      label: () => translate('Rating'),
      type: filterBuilderTypes.NUMBER
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
  'authorIndex.sortKey',
  'authorIndex.sortDirection',
  'authorIndex.selectedFilterKey',
  'authorIndex.customFilters',
  'authorIndex.view',
  'authorIndex.columns',
  'authorIndex.posterOptions',
  'authorIndex.bannerOptions',
  'authorIndex.overviewOptions',
  'authorIndex.tableOptions'
];

//
// Actions Types

export const SET_AUTHOR_SORT = 'authorIndex/setAuthorSort';
export const SET_AUTHOR_FILTER = 'authorIndex/setAuthorFilter';
export const SET_AUTHOR_VIEW = 'authorIndex/setAuthorView';
export const SET_AUTHOR_TABLE_OPTION = 'authorIndex/setAuthorTableOption';
export const SET_AUTHOR_POSTER_OPTION = 'authorIndex/setAuthorPosterOption';
export const SET_AUTHOR_BANNER_OPTION = 'authorIndex/setAuthorBannerOption';
export const SET_AUTHOR_OVERVIEW_OPTION = 'authorIndex/setAuthorOverviewOption';
export const SAVE_AUTHOR_EDITOR = 'authorIndex/saveAuthorEditor';
export const BULK_DELETE_AUTHOR = 'authorIndex/bulkDeleteAuthor';

//
// Action Creators

export const setAuthorSort = createAction(SET_AUTHOR_SORT);
export const setAuthorFilter = createAction(SET_AUTHOR_FILTER);
export const setAuthorView = createAction(SET_AUTHOR_VIEW);
export const setAuthorTableOption = createAction(SET_AUTHOR_TABLE_OPTION);
export const setAuthorPosterOption = createAction(SET_AUTHOR_POSTER_OPTION);
export const setAuthorBannerOption = createAction(SET_AUTHOR_BANNER_OPTION);
export const setAuthorOverviewOption = createAction(SET_AUTHOR_OVERVIEW_OPTION);
export const saveAuthorEditor = createThunk(SAVE_AUTHOR_EDITOR);
export const bulkDeleteAuthor = createThunk(BULK_DELETE_AUTHOR);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [SAVE_AUTHOR_EDITOR]: function(getState, payload, dispatch) {
    dispatch(set({
      section,
      isSaving: true,
      saveWarning: null
    }));

    const promise = createAjaxRequest({
      url: '/author/editor',
      method: 'PUT',
      data: JSON.stringify(payload),
      dataType: 'json'
    }).request;

    promise.done((data, textStatus, jqXHR) => {
      const saveWarning = jqXHR?.getResponseHeader('X-Chaptarr-Warning');

      dispatch(batchActions([
        ...data.map((author) => {
          return updateItem({
            id: author.id,
            section: 'authors',
            ...author
          });
        }),

        set({
          section,
          isSaving: false,
          saveError: null,
          saveWarning: saveWarning || null
        })
      ]));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isSaving: false,
        saveError: xhr,
        saveWarning: null
      }));
    });
  },

  [BULK_DELETE_AUTHOR]: function(getState, payload, dispatch) {
    dispatch(set({
      section,
      isDeleting: true
    }));

    const promise = createAjaxRequest({
      url: '/author/editor',
      method: 'DELETE',
      data: JSON.stringify(payload),
      dataType: 'json'
    }).request;

    promise.done(() => {
      // SignaR will take care of removing the author from the collection

      dispatch(set({
        section,
        isDeleting: false,
        deleteError: null
      }));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isDeleting: false,
        deleteError: xhr
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_AUTHOR_SORT]: createSetClientSideCollectionSortReducer(section),
  [SET_AUTHOR_FILTER]: createSetClientSideCollectionFilterReducer(section),

  [SET_AUTHOR_VIEW]: function(state, { payload }) {
    return Object.assign({}, state, { view: payload.view });
  },

  [SET_AUTHOR_TABLE_OPTION]: createSetTableOptionReducer(section),

  [SET_AUTHOR_POSTER_OPTION]: function(state, { payload }) {
    const posterOptions = state.posterOptions;

    return {
      ...state,
      posterOptions: {
        ...posterOptions,
        ...payload
      }
    };
  },

  [SET_AUTHOR_BANNER_OPTION]: function(state, { payload }) {
    const bannerOptions = state.bannerOptions;

    return {
      ...state,
      bannerOptions: {
        ...bannerOptions,
        ...payload
      }
    };
  },

  [SET_AUTHOR_OVERVIEW_OPTION]: function(state, { payload }) {
    const overviewOptions = state.overviewOptions;

    return {
      ...state,
      overviewOptions: {
        ...overviewOptions,
        ...payload
      }
    };
  }

}, defaultState, section);
