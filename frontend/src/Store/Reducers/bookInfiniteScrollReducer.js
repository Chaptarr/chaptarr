import { handleActions } from 'redux-actions';

//
// Action Types
//

export const FETCH_BOOK_BUCKETS_REQUEST = 'books/fetchBucketsRequest';
export const FETCH_BOOK_BUCKETS_SUCCESS = 'books/fetchBucketsSuccess';
export const FETCH_BOOK_BUCKETS_FAILURE = 'books/fetchBucketsFailure';

export const FETCH_BOOKS_PAGE_REQUEST = 'books/fetchPageRequest';
export const FETCH_BOOKS_PAGE_SUCCESS = 'books/fetchPageSuccess';
export const FETCH_BOOKS_PAGE_FAILURE = 'books/fetchPageFailure';

export const SET_BOOKS_ACTIVE_QUERY = 'books/setActiveQuery';
export const INVALIDATE_BOOKS_QUERY = 'books/invalidateQuery';
export const CLEAR_BOOKS_QUERIES = 'books/clearQueries';
export const ABORT_ALL_BOOK_REQUESTS = 'books/abortAllRequests';

//
// Initial State
//

export const initialState = {
  entities: {},
  queries: {},
  activeQueryKey: null,
  pageSize: 200
};

//
// Helpers
//

function createEmptyQuery(pageSize, queryParams = {}) {
  return {
    version: 0,
    lastAccessedAt: Date.now(),
    totalCount: null,
    footerStatistics: null,
    pageSize,
    sortKey: queryParams.sortKey || 'cleanTitle',
    sortDirection: queryParams.sortDirection || 'ascending',
    filters: queryParams.filters || {},
    buckets: {
      counts: {},
      cumulativeIndexes: {},
      order: [],
      status: 'idle',
      requestId: null,
      updatedAt: null
    },
    pages: {},
    latestRequests: {}
  };
}

function createEmptyPage() {
  return {
    status: 'idle',
    ids: [],
    error: null,
    requestId: null,
    requestedAt: null,
    receivedAt: null
  };
}

function shouldProcessResponse(state, queryKey, requestId, pageIndex = null) {
  const query = state.queries[queryKey];
  if (!query) {
    return false;
  }
  
  // For page requests, check page-specific request ID
  if (pageIndex !== null) {
    return query.latestRequests[pageIndex] === requestId;
  }
  
  // For bucket requests
  return query.buckets.requestId === requestId;
}

function cleanupOldQueries(state, maxQueries = 5, maxAge = 30 * 60 * 1000) {
  const now = Date.now();
  const queries = Object.entries(state.queries);
  
  // First, remove queries older than maxAge
  const activeQueries = queries.filter(([key, query]) => {
    return (now - query.lastAccessedAt) < maxAge;
  });
  
  // If still too many, keep only the most recent
  if (activeQueries.length > maxQueries) {
    activeQueries.sort((a, b) => b[1].lastAccessedAt - a[1].lastAccessedAt);
    const keepQueries = activeQueries.slice(0, maxQueries);
    
    const newQueries = {};
    keepQueries.forEach(([key, query]) => {
      newQueries[key] = query;
    });
    
    return {
      ...state,
      queries: newQueries,
      // Clear activeQueryKey if its query was removed
      activeQueryKey: newQueries[state.activeQueryKey] ? state.activeQueryKey : null
    };
  }
  
  // Convert back to object if we only did age filtering
  if (activeQueries.length !== queries.length) {
    const newQueries = {};
    activeQueries.forEach(([key, query]) => {
      newQueries[key] = query;
    });
    
    return {
      ...state,
      queries: newQueries,
      activeQueryKey: newQueries[state.activeQueryKey] ? state.activeQueryKey : null
    };
  }
  
  return state;
}

//
// Action Handlers
//

const actionHandlers = {
  
  // Bucket Actions
  [FETCH_BOOK_BUCKETS_REQUEST]: (state, { payload }) => {
    const { queryKey, requestId } = payload;
    
    const query = state.queries[queryKey] || createEmptyQuery(state.pageSize, {});
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          buckets: {
            ...query.buckets,
            status: 'loading',
            requestId,
            error: null
          }
        }
      }
    };
  },
  
  [FETCH_BOOK_BUCKETS_SUCCESS]: (state, { payload }) => {
    const { queryKey, requestId, buckets, totalCount, cumulativeIndexes, footerStatistics } = payload;
    
    if (!shouldProcessResponse(state, queryKey, requestId)) {
      return state;
    }
    
    const query = state.queries[queryKey];
    const bucketOrder = Object.keys(buckets).sort((a, b) => {
      // Special ordering: #, 0-9, A-Z
      if (a === '#') return -1;
      if (b === '#') return 1;
      if (a === '0-9' && b !== '#') return -1;
      if (b === '0-9' && a !== '#') return 1;
      return a.localeCompare(b);
    });
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          totalCount: totalCount === undefined ? query.totalCount : totalCount,
          footerStatistics: footerStatistics === undefined ? query.footerStatistics : footerStatistics,
          buckets: {
            counts: buckets,
            cumulativeIndexes: cumulativeIndexes || {},
            order: bucketOrder,
            status: 'succeeded',
            requestId,
            updatedAt: Date.now(),
            error: null
          }
        }
      }
    };
  },
  
  [FETCH_BOOK_BUCKETS_FAILURE]: (state, { payload }) => {
    const { queryKey, requestId, error } = payload;
    
    if (!shouldProcessResponse(state, queryKey, requestId)) {
      return state;
    }
    
    const query = state.queries[queryKey];
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          buckets: {
            ...query.buckets,
            status: 'failed',
            error: { message: error },
            updatedAt: Date.now()
          }
        }
      }
    };
  },
  
  // Page Actions
  [FETCH_BOOKS_PAGE_REQUEST]: (state, { payload }) => {
    const { queryKey, pageIndex, requestId } = payload;
    
    let query = state.queries[queryKey];
    if (!query) {
      query = createEmptyQuery(state.pageSize, {});
    }
    
    const page = query.pages[pageIndex] || createEmptyPage();
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          pages: {
            ...query.pages,
            [pageIndex]: {
              ...page,
              status: 'loading',
              requestId,
              requestedAt: Date.now(),
              error: null
            }
          },
          latestRequests: {
            ...query.latestRequests,
            [pageIndex]: requestId
          }
        }
      }
    };
  },
  
  [FETCH_BOOKS_PAGE_SUCCESS]: (state, { payload }) => {
    const { queryKey, pageIndex, requestId, ids, entities, totalCount, offset } = payload;
    
    if (!shouldProcessResponse(state, queryKey, requestId, pageIndex)) {
      return state;
    }
    
    const query = state.queries[queryKey];
    
    // Merge entities (last-write-wins)
    const newEntities = {
      ...state.entities
    };
    
    Object.keys(entities).forEach(id => {
      const existing = newEntities[id];
      const incoming = entities[id];
      
      // If we have updatedAt, use it for conflict resolution
      if (!existing || !existing.updatedAt || !incoming.updatedAt || 
          incoming.updatedAt >= existing.updatedAt) {
        newEntities[id] = incoming;
      }
    });
    
    // Remove duplicates within the page
    const uniqueIds = [...new Set(ids)];
    
    // Clean up old queries before updating
    const cleanedState = cleanupOldQueries(state);
    
    return {
      ...cleanedState,
      entities: newEntities,
      queries: {
        ...cleanedState.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          totalCount: totalCount === undefined ? query.totalCount : totalCount,
          pages: {
            ...query.pages,
            [pageIndex]: {
              status: 'succeeded',
              ids: uniqueIds,
              error: null,
              requestId,
              requestedAt: query.pages[pageIndex]?.requestedAt,
              receivedAt: Date.now()
            }
          }
        }
      }
    };
  },
  
  [FETCH_BOOKS_PAGE_FAILURE]: (state, { payload }) => {
    const { queryKey, pageIndex, requestId, error } = payload;
    
    if (!shouldProcessResponse(state, queryKey, requestId, pageIndex)) {
      return state;
    }
    
    const query = state.queries[queryKey];
    const page = query.pages[pageIndex];
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          lastAccessedAt: Date.now(),
          pages: {
            ...query.pages,
            [pageIndex]: {
              ...page,
              status: 'failed',
              error: { message: error },
              receivedAt: Date.now()
            }
          }
        }
      }
    };
  },
  
  // Query Management Actions
  [SET_BOOKS_ACTIVE_QUERY]: (state, { payload }) => {
    const { queryKey, queryParams } = typeof payload === 'string' ? { queryKey: payload, queryParams: {} } : payload;
    
    // Create query if it doesn't exist
    if (!state.queries[queryKey]) {
      return {
        ...state,
        queries: {
          ...state.queries,
          [queryKey]: createEmptyQuery(state.pageSize, queryParams)
        },
        activeQueryKey: queryKey
      };
    }
    
    // Update existing query with new parameters and lastAccessedAt
    const existingQuery = state.queries[queryKey];
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...existingQuery,
          lastAccessedAt: Date.now(),
          sortKey: queryParams?.sortKey || existingQuery.sortKey,
          sortDirection: queryParams?.sortDirection || existingQuery.sortDirection,
          filters: queryParams?.filters || existingQuery.filters
        }
      },
      activeQueryKey: queryKey
    };
  },
  
  [INVALIDATE_BOOKS_QUERY]: (state, { payload }) => {
    const queryKey = payload;
    const query = state.queries[queryKey];
    
    if (!query) {
      return state;
    }
    
    return {
      ...state,
      queries: {
        ...state.queries,
        [queryKey]: {
          ...query,
          version: query.version + 1,
          pages: {},
          latestRequests: {},
          buckets: {
            ...query.buckets,
            status: 'idle',
            requestId: null
          }
        }
      }
    };
  },
  
  [CLEAR_BOOKS_QUERIES]: (state, { payload }) => {
    const { clearEntities = false } = payload || {};
    
    return {
      ...state,
      entities: clearEntities ? {} : state.entities,
      queries: {},
      activeQueryKey: null
    };
  },

  [ABORT_ALL_BOOK_REQUESTS]: (state, { payload }) => {
    const { affectedRequests = [] } = payload;
    
    // If no affected requests, nothing to reset
    if (affectedRequests.length === 0) {
      return state;
    }
    
    const newQueries = { ...state.queries };
    
    // Reset loading states for each affected request
    affectedRequests.forEach(({ type, queryKey, pageIndex, requestId }) => {
      const query = newQueries[queryKey];
      if (!query) return;
      
      if (type === 'buckets') {
        // Reset bucket loading state if it matches the request ID
        if (query.buckets?.requestId === requestId && query.buckets?.status === 'loading') {
          newQueries[queryKey] = {
            ...query,
            buckets: {
              ...query.buckets,
              status: 'idle',
              requestId: null,
              error: null
            }
          };
        }
      } else if (type === 'page' && pageIndex !== null) {
        // Reset page loading state if it matches the request ID
        const page = query.pages?.[pageIndex];
        if (page?.requestId === requestId && page?.status === 'loading') {
          newQueries[queryKey] = {
            ...newQueries[queryKey], // Use updated query in case buckets were modified above
            pages: {
              ...newQueries[queryKey].pages,
              [pageIndex]: {
                ...page,
                status: 'idle',
                requestId: null,
                error: null
              }
            },
            latestRequests: {
              ...newQueries[queryKey].latestRequests,
              [pageIndex]: null
            }
          };
        }
      }
    });
    
    return {
      ...state,
      queries: newQueries
    };
  }
};

//
// Reducer
//

export default handleActions(actionHandlers, initialState);

//
// Selectors
//

export const selectBookEntities = (state) => state.bookInfiniteScroll.entities;

export const selectActiveQuery = (state) => {
  const { activeQueryKey, queries } = state.bookInfiniteScroll;
  return activeQueryKey ? queries[activeQueryKey] : null;
};

export const selectQueryBooks = (state, queryKey) => {
  const query = state.bookInfiniteScroll.queries[queryKey];
  if (!query) {
    return [];
  }
  
  const allIds = [];
  const seenIds = new Set();
  
  // Get page indexes in order
  const pageIndexes = Object.keys(query.pages)
    .map(Number)
    .sort((a, b) => a - b);
  
  // Flatten IDs, deduplicating across pages
  pageIndexes.forEach(index => {
    const page = query.pages[index];
    if (page.status === 'succeeded' && page.ids) {
      page.ids.forEach(id => {
        if (!seenIds.has(id)) {
          seenIds.add(id);
          allIds.push(id);
        }
      });
    }
  });
  
  // Map to actual book entities
  return allIds.map(id => state.bookInfiniteScroll.entities[id]).filter(Boolean);
};

export const selectQueryBuckets = (state, queryKey) => {
  const query = state.bookInfiniteScroll.queries[queryKey];
  return query?.buckets || null;
};

export const selectQueryHasMore = (state, queryKey) => {
  const query = state.bookInfiniteScroll.queries[queryKey];
  if (!query || query.totalCount === null) {
    return true; // Assume more until we know
  }
  
  const loadedCount = selectQueryBooks(state, queryKey).length;
  return loadedCount < query.totalCount;
};

export const selectPageStatus = (state, queryKey, pageIndex) => {
  const query = state.bookInfiniteScroll.queries[queryKey];
  const page = query?.pages?.[pageIndex];
  return page?.status || 'idle';
};
