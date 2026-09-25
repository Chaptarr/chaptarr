import { createAction } from 'redux-actions';
import { filterBuilderTypes, filterBuilderValueTypes, filterTypes, sortDirections } from 'Helpers/Props';
import { set } from 'Store/Actions/baseActions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import createHandleActions from './Creators/createHandleActions';
import createSetClientSideCollectionFilterReducer from './Creators/Reducers/createSetClientSideCollectionFilterReducer';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';

//
// Variables

export const section = 'releases';
export const bookSection = 'releases.book';
export const authorSection = 'releases.author';

let abortCurrentRequest = null;
const authorSearchAbortRequests = new Map();
const authorReleaseStopRequests = new Map();

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: [],
  hiddenItems: [],
  authorSearch: {
    searchId: null,
    authorId: null,
    mediaType: null,
    isFetching: false,
    isComplete: false,
    isCancelled: false,
    error: null,
    bookResults: [],
    expandedBookIds: {}
  },
  sortKey: 'releaseWeight',
  sortDirection: sortDirections.ASCENDING,
  bypassFilters: false,
  filterSummary: null,
  siblingBookId: null,
  siblingMediaType: null,
  siblingToggleEnabled: false,
  siblingToggleDisabledReason: null,
  sortPredicates: {
    age: function(item, direction) {
      return item.ageMinutes;
    },
    peers: function(item, direction) {
      const seeders = item.seeders || 0;
      const leechers = item.leechers || 0;

      return seeders * 1000000 + leechers;
    },
    rejections: function(item, direction) {
      const rejections = item.rejections;
      const releaseWeight = item.releaseWeight;

      if (rejections.length !== 0) {
        return releaseWeight + 1000000;
      }

      return releaseWeight;
    }
  },

  filters: [
    {
      key: 'all',
      label: 'All',
      filters: []
    },
    {
      key: 'discography-pack',
      label: 'Discography',
      filters: [
        {
          key: 'discography',
          value: true,
          type: filterTypes.EQUAL
        }
      ]
    },
    {
      key: 'not-discography-pack',
      label: 'Not Discography',
      filters: [
        {
          key: 'discography',
          value: false,
          type: filterTypes.EQUAL
        }
      ]
    }
  ],

  filterPredicates: {
    quality: function(item, value, type) {
      const qualityId = item.quality.quality.id;

      if (type === filterTypes.EQUAL) {
        return qualityId === value;
      }

      if (type === filterTypes.NOT_EQUAL) {
        return qualityId !== value;
      }

      // Default to false
      return false;
    },

    rejectionCount: function(item, value, type) {
      const rejectionCount = item.rejections.length;

      switch (type) {
        case filterTypes.EQUAL:
          return rejectionCount === value;

        case filterTypes.GREATER_THAN:
          return rejectionCount > value;

        case filterTypes.GREATER_THAN_OR_EQUAL:
          return rejectionCount >= value;

        case filterTypes.LESS_THAN:
          return rejectionCount < value;

        case filterTypes.LESS_THAN_OR_EQUAL:
          return rejectionCount <= value;

        case filterTypes.NOT_EQUAL:
          return rejectionCount !== value;

        default:
          return false;
      }
    },

    peers: function(item, value, type) {
      const seeders = item.seeders || 0;
      const leechers = item.leechers || 0;
      const peers = seeders + leechers;

      switch (type) {
        case filterTypes.EQUAL:
          return peers === value;

        case filterTypes.GREATER_THAN:
          return peers > value;

        case filterTypes.GREATER_THAN_OR_EQUAL:
          return peers >= value;

        case filterTypes.LESS_THAN:
          return peers < value;

        case filterTypes.LESS_THAN_OR_EQUAL:
          return peers <= value;

        case filterTypes.NOT_EQUAL:
          return peers !== value;

        default:
          return false;
      }
    }
  },

  filterBuilderProps: [
    {
      name: 'title',
      label: 'Title',
      type: filterBuilderTypes.STRING
    },
    {
      name: 'age',
      label: 'Age',
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'protocol',
      label: 'Protocol',
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.PROTOCOL
    },
    {
      name: 'indexerId',
      label: 'Indexer',
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.INDEXER
    },
    {
      name: 'size',
      label: 'Size',
      type: filterBuilderTypes.NUMBER,
      valueType: filterBuilderValueTypes.BYTES
    },
    {
      name: 'seeders',
      label: 'Seeders',
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'leechers',
      label: 'Peers',
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'quality',
      label: 'Quality',
      type: filterBuilderTypes.EXACT,
      valueType: filterBuilderValueTypes.QUALITY
    },
    {
      name: 'customFormatScore',
      label: () => translate('CustomFormatScore'),
      type: filterBuilderTypes.NUMBER
    },
    {
      name: 'rejectionCount',
      label: 'Rejection Count',
      type: filterBuilderTypes.NUMBER
    }
  ],

  book: {
    selectedFilterKey: 'all'
  },

  author: {
    selectedFilterKey: 'all'
  }
};

export const persistState = [
  'releases.selectedFilterKey',
  'releases.book.customFilters',
  'releases.author.customFilters'
];

//
// Actions Types

export const FETCH_RELEASES = 'releases/fetchReleases';
export const CANCEL_FETCH_RELEASES = 'releases/cancelFetchReleases';
export const SET_RELEASES_SORT = 'releases/setReleasesSort';
export const CLEAR_RELEASES = 'releases/clearReleases';
export const GRAB_RELEASE = 'releases/grabRelease';
export const UPDATE_RELEASE = 'releases/updateRelease';
export const SET_BOOK_RELEASES_FILTER = 'releases/setBookReleasesFilter';
export const SET_AUTHOR_RELEASES_FILTER = 'releases/setAuthorReleasesFilter';
export const SET_BYPASS_FILTERS = 'releases/setBypassFilters';
export const SET_FILTER_SUMMARY = 'releases/setFilterSummary';
export const SET_RELEASE_SEARCH_RESPONSE = 'releases/setReleaseSearchResponse';
export const FETCH_AUTHOR_RELEASES = 'releases/fetchAuthorReleases';
export const CANCEL_AUTHOR_RELEASES = 'releases/cancelAuthorReleases';
export const REQUEST_AUTHOR_RELEASE_STOP = 'releases/requestAuthorReleaseStop';
export const SET_AUTHOR_SEARCH_RESULTS = 'releases/setAuthorSearchResults';
export const CLEAR_AUTHOR_SEARCH = 'releases/clearAuthorSearch';
export const TOGGLE_AUTHOR_SEARCH_BOOK = 'releases/toggleAuthorSearchBook';

//
// Action Creators

export const fetchReleases = createThunk(FETCH_RELEASES);
export const cancelFetchReleases = createThunk(CANCEL_FETCH_RELEASES);
export const setReleasesSort = createAction(SET_RELEASES_SORT);
export const clearReleases = createAction(CLEAR_RELEASES);
export const grabRelease = createThunk(GRAB_RELEASE);
export const updateRelease = createAction(UPDATE_RELEASE);
export const setBookReleasesFilter = createAction(SET_BOOK_RELEASES_FILTER);
export const setAuthorReleasesFilter = createAction(SET_AUTHOR_RELEASES_FILTER);
export const setBypassFilters = createAction(SET_BYPASS_FILTERS);
export const setFilterSummary = createAction(SET_FILTER_SUMMARY);
export const setReleaseSearchResponse = createAction(SET_RELEASE_SEARCH_RESPONSE);
export const fetchAuthorReleases = createThunk(FETCH_AUTHOR_RELEASES);
export const cancelAuthorReleases = createThunk(CANCEL_AUTHOR_RELEASES);
export const requestAuthorReleaseStop = createThunk(REQUEST_AUTHOR_RELEASE_STOP);
export const setAuthorSearchResults = createAction(SET_AUTHOR_SEARCH_RESULTS);
export const clearAuthorSearch = createAction(CLEAR_AUTHOR_SEARCH);
export const toggleAuthorSearchBook = createAction(TOGGLE_AUTHOR_SEARCH_BOOK);

//
// Helpers

function parseReleaseSearchResponse(data) {
  let releases = data;
  let filterSummary = null;
  let hiddenReleases = [];
  let siblingBookId = null;
  let siblingMediaType = null;
  let siblingToggleEnabled = false;
  let siblingToggleDisabledReason = null;

  if (data && typeof data === 'object' && data.releases) {
    releases = data.releases;
    filterSummary = data.filterSummary;
    hiddenReleases = data.hiddenReleases || [];
    siblingBookId = data.siblingBookId ?? null;
    siblingMediaType = data.siblingMediaType ?? null;
    siblingToggleEnabled = data.siblingToggleEnabled === true;
    siblingToggleDisabledReason = data.siblingToggleDisabledReason ?? null;
  }

  return {
    items: releases || [],
    hiddenItems: hiddenReleases,
    filterSummary,
    siblingBookId,
    siblingMediaType,
    siblingToggleEnabled,
    siblingToggleDisabledReason
  };
}

function normalizeReleaseItems(items) {
  if (!Array.isArray(items)) {
    return [];
  }

  const malformedItemCount = items.filter((item) =>
    !Array.isArray(item.customFormats) || !Array.isArray(item.rejections)
  ).length;

  if (!malformedItemCount) {
    return items;
  }

  console.warn(`[InteractiveSearch] Normalized ${malformedItemCount} release result(s) with missing collection fields`);

  return items.map((item) => ({
    ...item,
    customFormats: Array.isArray(item.customFormats) ? item.customFormats : [],
    rejections: Array.isArray(item.rejections) ? item.rejections : []
  }));
}

function getReleaseKey(item) {
  return `${item.indexerId ?? ''}:${item.guid ?? ''}`;
}

function mergeGrabState(previous, incoming) {
  if (!previous) {
    return incoming;
  }

  return {
    ...previous,
    ...incoming,
    isGrabbing: previous.isGrabbing === true || incoming.isGrabbing === true,
    isGrabbed: previous.isGrabbed === true || incoming.isGrabbed === true,
    grabError: previous.grabError ?? incoming.grabError ?? null
  };
}

function mergeReleaseItems(previousItems, incomingItems) {
  const previousByKey = new Map((previousItems || []).map((item) => [getReleaseKey(item), item]));

  return normalizeReleaseItems(incomingItems).map((item) => mergeGrabState(previousByKey.get(getReleaseKey(item)), item));
}

function mergeAuthorBookResults(previousResults, incomingResults) {
  const results = [...(previousResults || [])];

  (Array.isArray(incomingResults) ? incomingResults : []).forEach((incoming) => {
    const incomingId = String(incoming.bookId);
    const index = results.findIndex((result) => String(result.bookId) === incomingId);
    const previous = index === -1 ? null : results[index];
    const merged = {
      ...previous,
      ...incoming,
      releases: mergeReleaseItems(previous?.releases, incoming.releases),
      hiddenReleases: mergeReleaseItems(previous?.hiddenReleases, incoming.hiddenReleases)
    };

    if (index === -1) {
      results.push(merged);
    } else {
      results[index] = merged;
    }
  });

  return results;
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [FETCH_RELEASES]: function(getState, payload, dispatch) {
    const state = getState();
    const bypassFilters = state.releases.bypassFilters;

    // Include bypassFilters in the API request payload
    const enhancedPayload = {
      ...payload,
      bypassFilters
    };

    dispatch(set({
      section,
      isFetching: true,
      isPopulated: false,
      error: null,
      items: [],
      hiddenItems: [],
      filterSummary: null,
      siblingBookId: null,
      siblingMediaType: null,
      siblingToggleEnabled: false,
      siblingToggleDisabledReason: null
    }));

    const { request, abortRequest } = createAjaxRequest({
      url: payload.id == null ? '/release' : `/release/${payload.id}`,
      data: enhancedPayload,
      traditional: true
    });

    request.done((data) => {
      dispatch(setReleaseSearchResponse(parseReleaseSearchResponse(data)));
    });

    request.fail((xhr) => {
      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });

    abortCurrentRequest = abortRequest;
  },

  [CANCEL_FETCH_RELEASES]: function(getState, payload, dispatch) {
    if (abortCurrentRequest) {
      abortCurrentRequest = abortCurrentRequest();
    }
  },

  [FETCH_AUTHOR_RELEASES]: function(getState, payload, dispatch) {
    const { searchId, authorId, mediaType } = payload;

    dispatch(set({
      section,
      authorSearch: {
        ...defaultState.authorSearch,
        searchId,
        authorId,
        mediaType,
        isFetching: true
      }
    }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/release',
      data: { authorId, mediaType, searchId },
      traditional: true
    });

    authorSearchAbortRequests.set(searchId, abortRequest);

    request.done((data) => {
      authorSearchAbortRequests.delete(searchId);
      dispatch(setAuthorSearchResults({
        ...data,
        allowTerminalMerge: true,
        requestFinished: true
      }));
    });

    request.fail((xhr) => {
      authorSearchAbortRequests.delete(searchId);

      if (!xhr.aborted) {
        dispatch(setAuthorSearchResults({
          searchId,
          authorId,
          mediaType,
          requestFinished: true,
          requestFailed: true,
          error: xhr
        }));
      }
    });
  },

  [CANCEL_AUTHOR_RELEASES]: function(getState, payload) {
    const abortRequest = authorSearchAbortRequests.get(payload.searchId);

    if (abortRequest) {
      authorSearchAbortRequests.delete(payload.searchId);
      abortRequest();
    }
  },

  [REQUEST_AUTHOR_RELEASE_STOP]: function(getState, payload) {
    const searchId = payload.searchId;
    const existingRequest = authorReleaseStopRequests.get(searchId);

    if (existingRequest) {
      return existingRequest;
    }

    const request = createAjaxRequest({
      url: `/release?searchId=${encodeURIComponent(searchId)}`,
      method: 'DELETE',
      dataType: 'json'
    }).request;

    authorReleaseStopRequests.set(searchId, request);
    request.always(() => {
      if (authorReleaseStopRequests.get(searchId) === request) {
        authorReleaseStopRequests.delete(searchId);
      }
    });

    return request;
  },

  [GRAB_RELEASE]: function(getState, payload, dispatch) {
    const guid = payload.guid;
    const indexerId = payload.indexerId;

    const releaseIdentity = {
      guid,
      indexerId,
      searchId: payload.searchId,
      searchedBookId: payload.searchedBookId
    };

    dispatch(updateRelease({ ...releaseIdentity, isGrabbing: true }));

    const promise = createAjaxRequest({
      url: '/release',
      method: 'POST',
      contentType: 'application/json',
      dataType: 'json',
      data: JSON.stringify(payload)
    }).request;

    promise.done((data) => {
      dispatch(updateRelease({
        ...releaseIdentity,
        isGrabbing: false,
        isGrabbed: true,
        grabError: null
      }));
    });

    promise.fail((xhr) => {
      const grabError = xhr.responseJSON && xhr.responseJSON.message || 'Failed to add to download queue';

      dispatch(updateRelease({
        ...releaseIdentity,
        isGrabbing: false,
        isGrabbed: false,
        grabError
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_RELEASES]: (state) => {
    const {
      book,
      author,
      authorSearch,
      ...otherDefaultState
    } = defaultState;

    return Object.assign({}, state, otherDefaultState, { authorSearch: state.authorSearch || authorSearch });
  },

  [SET_BYPASS_FILTERS]: (state, { payload }) => {
    return Object.assign({}, state, { bypassFilters: payload.bypassFilters });
  },

  [SET_FILTER_SUMMARY]: (state, { payload }) => {
    return Object.assign({}, state, { filterSummary: payload.filterSummary });
  },

  [SET_RELEASE_SEARCH_RESPONSE]: (state, { payload }) => {
    return Object.assign({}, state, {
      items: normalizeReleaseItems(payload.items),
      hiddenItems: normalizeReleaseItems(payload.hiddenItems),
      filterSummary: payload.filterSummary || null,
      siblingBookId: payload.siblingBookId ?? null,
      siblingMediaType: payload.siblingMediaType ?? null,
      siblingToggleEnabled: payload.siblingToggleEnabled === true,
      siblingToggleDisabledReason: payload.siblingToggleDisabledReason ?? null,
      isFetching: false,
      isPopulated: true,
      error: null
    });
  },

  [SET_AUTHOR_SEARCH_RESULTS]: (state, { payload }) => {
    const current = state.authorSearch;

    if (
      !current.searchId ||
      payload.searchId !== current.searchId ||
      payload.authorId !== current.authorId ||
      payload.mediaType !== current.mediaType ||
      (current.isComplete && payload.allowTerminalMerge !== true)
    ) {
      return state;
    }

    if (payload.requestFailed === true) {
      return Object.assign({}, state, {
        authorSearch: {
          ...current,
          isFetching: false,
          isComplete: false,
          error: payload.error || true
        }
      });
    }

    const bookResults = mergeAuthorBookResults(current.bookResults, payload.bookResults);
    const isComplete = current.isComplete || payload.isComplete === true;

    return Object.assign({}, state, {
      authorSearch: {
        ...current,
        bookResults,
        isFetching: payload.requestFinished === true || isComplete ? false : current.isFetching,
        isComplete,
        isCancelled: current.isCancelled || payload.isCancelled === true,
        error: isComplete ? null : current.error
      }
    });
  },

  [CLEAR_AUTHOR_SEARCH]: (state, { payload }) => {
    const current = state.authorSearch;

    if (
      !current.searchId ||
      payload.searchId !== current.searchId ||
      payload.authorId !== current.authorId ||
      payload.mediaType !== current.mediaType
    ) {
      return state;
    }

    return Object.assign({}, state, { authorSearch: defaultState.authorSearch });
  },

  [TOGGLE_AUTHOR_SEARCH_BOOK]: (state, { payload }) => {
    const current = state.authorSearch;

    if (!current.searchId || payload.searchId !== current.searchId) {
      return state;
    }

    return Object.assign({}, state, {
      authorSearch: {
        ...current,
        expandedBookIds: {
          ...current.expandedBookIds,
          [payload.bookId]: !current.expandedBookIds[payload.bookId]
        }
      }
    });
  },

  [UPDATE_RELEASE]: (state, { payload }) => {
    const guid = payload.guid;
    const indexerId = payload.indexerId;

    if (payload.searchId != null && payload.searchedBookId != null) {
      const authorSearch = state.authorSearch;

      if (authorSearch.searchId !== payload.searchId) {
        return state;
      }

      const groupIndex = authorSearch.bookResults.findIndex((result) => String(result.bookId) === String(payload.searchedBookId));

      if (groupIndex === -1) {
        return state;
      }

      const group = authorSearch.bookResults[groupIndex];
      const updateGroupList = (list) => {
        const itemIndex = list.findIndex((item) => item.guid === guid && item.indexerId === indexerId);

        if (itemIndex === -1) {
          return list;
        }

        const updated = [...list];
        updated[itemIndex] = { ...updated[itemIndex], ...payload };
        return updated;
      };
      const nextGroup = {
        ...group,
        releases: updateGroupList(group.releases || []),
        hiddenReleases: updateGroupList(group.hiddenReleases || [])
      };

      if (nextGroup.releases === group.releases && nextGroup.hiddenReleases === group.hiddenReleases) {
        return state;
      }

      const bookResults = [...authorSearch.bookResults];
      bookResults[groupIndex] = nextGroup;

      return Object.assign({}, state, {
        authorSearch: { ...authorSearch, bookResults }
      });
    }

    const updateList = (list) => {
      if (!list || !list.length) {
        return { updated: false, list };
      }

      const index = list.findIndex((item) => {
        if (item.guid !== guid) {
          return false;
        }

        // Prefer indexerId matching when available to avoid guid collisions across indexers.
        if (indexerId == null) {
          return true;
        }

        return item.indexerId === indexerId;
      });

      if (index === -1) {
        return { updated: false, list };
      }

      const item = Object.assign({}, list[index], payload);
      const updatedList = [...list];
      updatedList.splice(index, 1, item);

      return { updated: true, list: updatedList };
    };

    const itemsUpdate = updateList(state.items);
    const hiddenItemsUpdate = itemsUpdate.updated ? { updated: false, list: state.hiddenItems } : updateList(state.hiddenItems);

    if (!itemsUpdate.updated && !hiddenItemsUpdate.updated) {
      return state;
    }

    return Object.assign({}, state, {
      items: itemsUpdate.list,
      hiddenItems: hiddenItemsUpdate.list
    });
  },

  [SET_RELEASES_SORT]: createSetClientSideCollectionSortReducer(section),
  [SET_BOOK_RELEASES_FILTER]: createSetClientSideCollectionFilterReducer(bookSection),
  [SET_AUTHOR_RELEASES_FILTER]: createSetClientSideCollectionFilterReducer(authorSection)

}, defaultState, section);
