import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { filterBuilderTypes, filterBuilderValueTypes, filterTypePredicates, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import { isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { SET_SELECTED_MEDIA_TYPE } from './appActions';
import { filterPredicates, filters } from './authorActions';
import { set, update } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionFilterReducer from './Creators/Reducers/createSetClientSideCollectionFilterReducer';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';

//
// Variables

export const section = 'bookshelf';

// Shelf rows load their books in batches as they scroll into view, so a large
// library never has to send every book at once.
const BOOK_BATCH_DELAY_MS = 50;
const MAX_AUTHORS_PER_BOOK_REQUEST = 50;

let pendingAuthorIds = { audiobook: new Set(), ebook: new Set() };
let requestedBookKeys = new Set();
let pendingFlushTimeout = null;
let bookRequestGeneration = 0;
let abortAuthorCountsRequest = null;

//
// State

export const defaultState = {
  isSaving: false,
  saveError: null,
  isFetching: false,
  isPopulated: false,
  error: null,
  authorBookCounts: {},
  authorBookCountsMediaType: null,
  loadedBookKeys: {},
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
export const FETCH_BOOKSHELF_AUTHORS = 'bookshelf/fetchBookshelfAuthors';
export const FETCH_BOOKSHELF_BOOKS = 'bookshelf/fetchBookshelfBooks';
export const CLEAR_BOOKSHELF = 'bookshelf/clearBookshelf';

//
// Action Creators

export const setBookshelfSort = createAction(SET_BOOKSHELF_SORT);
export const setBookshelfFilter = createAction(SET_BOOKSHELF_FILTER);
export const saveBookshelf = createThunk(SAVE_BOOKSHELF);
export const fetchBookshelfAuthors = createThunk(FETCH_BOOKSHELF_AUTHORS);
export const fetchBookshelfBooks = createThunk(FETCH_BOOKSHELF_BOOKS);
export const clearBookshelf = createThunk(CLEAR_BOOKSHELF);

//
// Helpers

export function getBookshelfBookKey(mediaType, authorId) {
  return `${mediaType}-${authorId}`;
}

function requestBookshelfBooks(getState, dispatch, authorIds, mediaType, generation) {
  const { request } = createAjaxRequest({
    url: '/book/shelf',
    data: {
      authorIds,
      mediaType
    },
    traditional: true
  });

  request.done((data) => {
    if (generation !== bookRequestGeneration) {
      return;
    }

    // Replace these authors' books for this media type, and keep everything else.
    const authorIdSet = new Set(authorIds);
    const otherBooks = getState().books.items.filter((book) => {
      return !(authorIdSet.has(book.authorId) && book.mediaType === mediaType);
    });

    const loadedBookKeys = { ...getState().bookshelf.loadedBookKeys };
    authorIds.forEach((authorId) => {
      loadedBookKeys[getBookshelfBookKey(mediaType, authorId)] = true;
    });

    dispatch(batchActions([
      update({ section: 'books', data: otherBooks.concat(data) }),
      set({ section, loadedBookKeys })
    ]));
  });

  request.fail(() => {
    if (generation !== bookRequestGeneration) {
      return;
    }

    // Let the rows ask again the next time they render.
    authorIds.forEach((authorId) => {
      requestedBookKeys.delete(getBookshelfBookKey(mediaType, authorId));
    });
  });
}

function flushBookshelfBookRequests(getState, dispatch) {
  pendingFlushTimeout = null;

  Object.keys(pendingAuthorIds).forEach((mediaType) => {
    const authorIds = Array.from(pendingAuthorIds[mediaType]);
    pendingAuthorIds[mediaType] = new Set();

    for (let i = 0; i < authorIds.length; i += MAX_AUTHORS_PER_BOOK_REQUEST) {
      const batch = authorIds.slice(i, i + MAX_AUTHORS_PER_BOOK_REQUEST);
      requestBookshelfBooks(getState, dispatch, batch, mediaType, bookRequestGeneration);
    }
  });
}

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_BOOKSHELF_AUTHORS]: function(getState, payload, dispatch) {
    const { mediaType } = payload;

    if (abortAuthorCountsRequest) {
      abortAuthorCountsRequest();
      abortAuthorCountsRequest = null;
    }

    // Counts for the other media type would list the wrong authors while this loads.
    const isSameMediaType = getState().bookshelf.authorBookCountsMediaType === mediaType;

    dispatch(set({
      section,
      isFetching: true,
      isPopulated: isSameMediaType && getState().bookshelf.isPopulated
    }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/book/shelf/authors',
      data: { mediaType }
    });

    abortAuthorCountsRequest = abortRequest;

    request.done((data) => {
      abortAuthorCountsRequest = null;

      const authorBookCounts = {};
      data.forEach((count) => {
        authorBookCounts[count.authorId] = count.bookCount;
      });

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: true,
        error: null,
        authorBookCounts,
        authorBookCountsMediaType: mediaType
      }));
    });

    request.fail((xhr) => {
      abortAuthorCountsRequest = null;

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });
  },

  [FETCH_BOOKSHELF_BOOKS]: function(getState, payload, dispatch) {
    const { authorId, mediaType } = payload;
    const key = getBookshelfBookKey(mediaType, authorId);

    if (requestedBookKeys.has(key)) {
      return;
    }

    requestedBookKeys.add(key);
    pendingAuthorIds[mediaType].add(authorId);

    if (!pendingFlushTimeout) {
      pendingFlushTimeout = setTimeout(() => {
        flushBookshelfBookRequests(getState, dispatch);
      }, BOOK_BATCH_DELAY_MS);
    }
  },

  [CLEAR_BOOKSHELF]: function(getState, payload, dispatch) {
    // Responses from before the clear are dropped by the generation check.
    bookRequestGeneration++;
    clearTimeout(pendingFlushTimeout);
    pendingFlushTimeout = null;
    pendingAuthorIds = { audiobook: new Set(), ebook: new Set() };
    requestedBookKeys = new Set();

    if (abortAuthorCountsRequest) {
      abortAuthorCountsRequest();
      abortAuthorCountsRequest = null;
    }

    dispatch(set({
      section,
      isFetching: false,
      isPopulated: false,
      error: null,
      authorBookCounts: {},
      authorBookCountsMediaType: null,
      loadedBookKeys: {}
    }));
  },

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
