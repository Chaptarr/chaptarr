import { createAction } from 'redux-actions';
import bookInfiniteScrollReducer, { initialState } from 'Store/Reducers/bookInfiniteScrollReducer';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';

//
// Variables
//

export const section = 'bookInfiniteScroll';

// In-flight request tracking to prevent duplicate requests
const inflightRequests = new Map();
const abortControllers = new Map();

//
// Helpers
//

function buildQueryParams(query, extraParams = {}) {
  const params = { ...extraParams };
  params.include = 'author';

  // Map frontend monitored filter to backend includeUnmonitored parameter
  if (query?.filters?.monitored === undefined) {
    // Default: include unmonitored books (show all)
    params.includeUnmonitored = true;
  } else {
    params.monitored = query.filters.monitored;
    params.includeUnmonitored = true;
  }

  // Add sorting parameters
  if (query?.sortKey) {
    params.sortKey = query.sortKey;
  }

  if (query?.sortDirection) {
    params.sortDirection = query.sortDirection === 'ascending' ? 'ASC' : 'DESC';
  }

  // Add other filters as needed
  if (query?.filters?.mediaType) {
    params.mediaType = query.filters.mediaType;
  }

  if (query?.filters?.downloaded !== undefined) {
    params.downloaded = query.filters.downloaded;
  }

  if (query?.filters?.missing !== undefined) {
    params.missing = query.filters.missing;
  }

  if (query?.filters?.wanted !== undefined) {
    params.wanted = query.filters.wanted;
  }

  return params;
}

//
// Action Types
//

export const FETCH_BOOK_BUCKETS_REQUEST = 'books/fetchBucketsRequest';
export const FETCH_BOOK_BUCKETS_SUCCESS = 'books/fetchBucketsSuccess';
export const FETCH_BOOK_BUCKETS_FAILURE = 'books/fetchBucketsFailure';

export const FETCH_BOOKS_PAGE_REQUEST = 'books/fetchPageRequest';
export const FETCH_BOOKS_PAGE_SUCCESS = 'books/fetchPageSuccess';
export const FETCH_BOOKS_PAGE_FAILURE = 'books/fetchPageFailure';
export const FETCH_BOOK_IDS_REQUEST = 'books/fetchIdsRequest';

export const SET_BOOKS_ACTIVE_QUERY = 'books/setActiveQuery';
export const INVALIDATE_BOOKS_QUERY = 'books/invalidateQuery';
export const CLEAR_BOOKS_QUERIES = 'books/clearQueries';
export const FETCH_BOOKS_INDEX_RANGE = 'books/fetchIndexRange';
export const JUMP_TO_LETTER = 'books/jumpToLetter';
export const ABORT_ALL_BOOK_REQUESTS = 'books/abortAllRequests';

//
// Action Creators
//

export const setActiveQuery = createAction(SET_BOOKS_ACTIVE_QUERY);
export const invalidateQuery = createAction(INVALIDATE_BOOKS_QUERY);
export const clearQueries = createAction(CLEAR_BOOKS_QUERIES);

export const fetchBookBuckets = createThunk(FETCH_BOOK_BUCKETS_REQUEST);

export const fetchBooksPage = createThunk(FETCH_BOOKS_PAGE_REQUEST);

export const fetchBookIds = createThunk(FETCH_BOOK_IDS_REQUEST);

export const fetchBooksForIndexRange = createThunk(FETCH_BOOKS_INDEX_RANGE);

export const jumpToLetter = createThunk(JUMP_TO_LETTER);

export const abortAllRequests = createThunk(ABORT_ALL_BOOK_REQUESTS);

//
// Action Handlers
//

export const actionHandlers = handleThunks({
  [FETCH_BOOK_BUCKETS_REQUEST]: function(getState, payload, dispatch) {
    const { queryKey } = payload;
    const requestKey = `buckets:${queryKey}`;

    // Check if already in flight
    if (inflightRequests.has(requestKey)) {
      return inflightRequests.get(requestKey);
    }

    // Abort any previous request for this key
    if (abortControllers.has(requestKey)) {
      abortControllers.get(requestKey).abort();
    }

    const state = getState().bookInfiniteScroll;
    const query = state.queries[queryKey];
    const params = buildQueryParams(query);

    dispatch({
      type: FETCH_BOOK_BUCKETS_REQUEST,
      payload: {
        queryKey,
        requestId: requestKey
      }
    });

    const controller = new AbortController();
    abortControllers.set(requestKey, controller);

    const promise = createAjaxRequest({
      url: '/book/buckets',
      data: params,
      signal: controller.signal
    }).request;

    promise.done((response) => {
      dispatch({
        type: FETCH_BOOK_BUCKETS_SUCCESS,
        payload: {
          queryKey,
          requestId: requestKey,
          buckets: response.buckets,
          totalCount: response.totalCount,
          cumulativeIndexes: response.cumulativeIndexes,
          footerStatistics: response.footerStatistics
        }
      });
    });

    promise.fail((xhr) => {
      if (xhr.statusText !== 'abort') {
        dispatch({
          type: FETCH_BOOK_BUCKETS_FAILURE,
          payload: {
            queryKey,
            requestId: requestKey,
            error: xhr.responseText || 'Failed to fetch buckets'
          }
        });
      }
    });

    promise.always(() => {
      inflightRequests.delete(requestKey);
      abortControllers.delete(requestKey);
    });

    inflightRequests.set(requestKey, promise);
    return promise;
  },

  [FETCH_BOOKS_PAGE_REQUEST]: function(getState, payload, dispatch) {
    const { queryKey, pageIndex } = payload;
    const state = getState().bookInfiniteScroll;
    const query = state.queries[queryKey];
    const pageSize = state.pageSize || 200;

    // Check if page is already loaded or loading
    const page = query?.pages?.[pageIndex];
    if (page?.status === 'loading' || page?.status === 'succeeded') {
      return Promise.resolve();
    }

    const params = buildQueryParams(query, {
      offset: pageIndex * pageSize,
      pageSize
    });

    const requestKey = `page:${queryKey}:${pageIndex}`;

    // Check if already in flight
    if (inflightRequests.has(requestKey)) {
      return inflightRequests.get(requestKey);
    }

    // Abort any previous request for this key
    if (abortControllers.has(requestKey)) {
      abortControllers.get(requestKey).abort();
    }

    dispatch({
      type: FETCH_BOOKS_PAGE_REQUEST,
      payload: {
        queryKey,
        pageIndex,
        requestId: requestKey
      }
    });

    const controller = new AbortController();
    abortControllers.set(requestKey, controller);

    const promise = createAjaxRequest({
      url: '/book/paged',
      data: params,
      signal: controller.signal
    }).request;

    promise.done((response) => {
      // Normalize books into entities
      const entities = {};
      const ids = [];

      response.records.forEach((book) => {
        entities[book.id] = book;
        ids.push(book.id);
      });

      dispatch({
        type: FETCH_BOOKS_PAGE_SUCCESS,
        payload: {
          queryKey,
          pageIndex,
          requestId: requestKey,
          ids,
          entities,
          totalCount: response.totalCount,
          offset: response.offset
        }
      });
    });

    promise.fail((xhr) => {
      if (xhr.statusText !== 'abort') {
        dispatch({
          type: FETCH_BOOKS_PAGE_FAILURE,
          payload: {
            queryKey,
            pageIndex,
            requestId: requestKey,
            error: xhr.responseText || 'Failed to fetch page'
          }
        });
      }
    });

    promise.always(() => {
      inflightRequests.delete(requestKey);
      abortControllers.delete(requestKey);
    });

    inflightRequests.set(requestKey, promise);
    return promise;
  },

  [FETCH_BOOK_IDS_REQUEST]: function(getState, payload, dispatch) {
    const { queryKey, queryParams } = payload;
    const state = getState().bookInfiniteScroll;
    const query = state.queries[queryKey] || queryParams;
    const params = buildQueryParams(query);

    return createAjaxRequest({
      url: '/book/ids',
      data: params
    }).request.then((response) => response);
  },

  [FETCH_BOOKS_INDEX_RANGE]: function(getState, payload, dispatch) {
    const { queryKey, startIndex, stopIndex } = payload;
    const state = getState().bookInfiniteScroll;
    const pageSize = state.pageSize || 200;

    const startPage = Math.floor(startIndex / pageSize);
    const endPage = Math.floor(stopIndex / pageSize);

    const promises = [];

    for (let pageIndex = startPage; pageIndex <= endPage; pageIndex++) {
      promises.push(dispatch(fetchBooksPage({ queryKey, pageIndex })));
    }

    return Promise.all(promises);
  },

  [JUMP_TO_LETTER]: function(getState, payload, dispatch) {
    const { queryKey, letter } = payload;
    const state = getState().bookInfiniteScroll;
    const query = state.queries[queryKey];

    if (!query?.buckets || query.buckets.status !== 'succeeded') {
      // Need to fetch buckets first
      return dispatch(fetchBookBuckets({ queryKey }))
        .then(() => {
          const updatedQuery = getState().bookInfiniteScroll.queries[queryKey];
          const targetIndex = updatedQuery?.buckets?.cumulativeIndexes?.[letter] || 0;

          // Prefetch pages around the target index
          const pageSize = state.pageSize || 200;
          const targetPage = Math.floor(targetIndex / pageSize);

          return dispatch(fetchBooksPage({ queryKey, pageIndex: targetPage }))
            .then(() => ({ targetIndex }));
        });
    }

    const targetIndex = query.buckets.cumulativeIndexes[letter] || 0;
    const pageSize = state.pageSize || 200;
    const targetPage = Math.floor(targetIndex / pageSize);

    // Prefetch the target page if not already loaded
    return dispatch(fetchBooksPage({ queryKey, pageIndex: targetPage }))
      .then(() => ({ targetIndex }));
  },

  [ABORT_ALL_BOOK_REQUESTS]: function(getState, payload, dispatch) {
    // Collect affected request keys before aborting
    const affectedRequests = [];
    inflightRequests.forEach((promise, requestKey) => {
      const [type, queryKey, pageIndex] = requestKey.split(':');
      affectedRequests.push({
        type: type === 'buckets' ? 'buckets' : 'page',
        queryKey,
        pageIndex: pageIndex ? parseInt(pageIndex) : null,
        requestId: requestKey
      });
    });

    // Abort all controllers and clear tracking
    abortControllers.forEach((controller) => controller.abort());
    abortControllers.clear();
    inflightRequests.clear();

    // Single dispatch to reset all affected loading states
    if (affectedRequests.length > 0) {
      dispatch({
        type: ABORT_ALL_BOOK_REQUESTS,
        payload: { affectedRequests }
      });
    }
  }
});

//
// Reducers
//

export const defaultState = initialState;
export const reducers = bookInfiniteScrollReducer;
