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

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: [],
  hiddenItems: [],
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

  [GRAB_RELEASE]: function(getState, payload, dispatch) {
    const guid = payload.guid;
    const indexerId = payload.indexerId;

    dispatch(updateRelease({ guid, indexerId, isGrabbing: true }));

    const promise = createAjaxRequest({
      url: '/release',
      method: 'POST',
      contentType: 'application/json',
      dataType: 'json',
      data: JSON.stringify(payload)
    }).request;

    promise.done((data) => {
      dispatch(updateRelease({
        guid,
        indexerId,
        isGrabbing: false,
        isGrabbed: true,
        grabError: null
      }));
    });

    promise.fail((xhr) => {
      const grabError = xhr.responseJSON && xhr.responseJSON.message || 'Failed to add to download queue';

      dispatch(updateRelease({
        guid,
        indexerId,
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
      ...otherDefaultState
    } = defaultState;

    return Object.assign({}, state, otherDefaultState);
  },

  [SET_BYPASS_FILTERS]: (state, { payload }) => {
    return Object.assign({}, state, { bypassFilters: payload.bypassFilters });
  },

  [SET_FILTER_SUMMARY]: (state, { payload }) => {
    return Object.assign({}, state, { filterSummary: payload.filterSummary });
  },

  [SET_RELEASE_SEARCH_RESPONSE]: (state, { payload }) => {
    return Object.assign({}, state, {
      items: payload.items || [],
      hiddenItems: payload.hiddenItems || [],
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

  [UPDATE_RELEASE]: (state, { payload }) => {
    const guid = payload.guid;
    const indexerId = payload.indexerId;

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
