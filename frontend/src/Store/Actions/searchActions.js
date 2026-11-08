import _ from 'lodash';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import orderAuthorSearchResults from 'Search/orderAuthorSearchResults';
import { createThunk, handleThunks } from 'Store/thunks';
import monitorNewItemsOptions, { normalizeMonitorNewItemsOption } from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions, { normalizeMonitorOption } from 'Utilities/Author/monitorOptions';
import getNewBook from 'Utilities/Book/getNewBook';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import updateSectionState from 'Utilities/State/updateSectionState';
import translate from 'Utilities/String/translate';
import { showMessage } from './appActions';
import { set, update, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'search';
let abortCurrentRequest = null;
let currentSearchRequestId = 0;

//
// State

export const bookMonitoringDefaults = {
  monitor: 'specificBook',
  audiobookMonitored: true,
  ebookMonitored: true,
  audiobookMonitor: 'specificBook',
  ebookMonitor: 'specificBook',
  monitorNewItems: 'none',
  audiobookMonitorNewItems: 'none',
  ebookMonitorNewItems: 'none'
};

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  isAdding: false,
  isAdded: false,
  // Distinguish queued vs added states for author adds
  isQueued: false,
  addNotice: null,
  pendingId: null,
  addedMediaTypes: [],
  addFailedMediaType: null,
  addError: null,
  items: [],

  authorDefaults: {
    audiobookRootFolderPath: '',
    ebookRootFolderPath: '',
    audiobookMonitored: null,
    ebookMonitored: null,
    monitor: monitorOptions[0].key,
    audiobookMonitor: null,
    ebookMonitor: null,
    monitorNewItems: monitorNewItemsOptions[0].key,
    audiobookMonitorNewItems: null,
    ebookMonitorNewItems: null,
    audiobookQualityProfileId: 0,
    ebookQualityProfileId: 0,
    audiobookMetadataProfileId: 0,
    ebookMetadataProfileId: 0,
    metadataProfileId: 0,
    tags: [],
    audiobookTags: null,
    ebookTags: null
  },

  bookDefaults: {
    ...bookMonitoringDefaults,
    rootFolderPath: '',
    audiobookRootFolderPath: '',
    ebookRootFolderPath: '',
    qualityProfileId: 0,
    audiobookQualityProfileId: 0,
    ebookQualityProfileId: 0,
    metadataProfileId: 0,
    audiobookMetadataProfileId: 0,
    ebookMetadataProfileId: 0,
    tags: [],
    audiobookTags: null,
    ebookTags: null
  }
};

export const persistState = [
  'search.bookDefaults',
  'search.authorDefaults'
];

//
// Actions Types

export const GET_SEARCH_RESULTS = 'search/getSearchResults';
export const ADD_AUTHOR = 'search/addAuthor';
export const ADD_BOOK = 'search/addBook';
export const CLEAR_SEARCH_RESULTS = 'search/clearSearchResults';
export const RESET_ADD_STATE = 'search/resetAddState';
export const SET_AUTHOR_ADD_DEFAULT = 'search/setAuthorAddDefault';
export const SET_BOOK_ADD_DEFAULT = 'search/setBookAddDefault';
export const UPDATE_SERIES_ENRICHMENT = 'search/UPDATE_SERIES_ENRICHMENT';

//
// Action Creators

export const getSearchResults = createThunk(GET_SEARCH_RESULTS);
export const addAuthor = createThunk(ADD_AUTHOR);
export const addBook = createThunk(ADD_BOOK);
export const clearSearchResults = createAction(CLEAR_SEARCH_RESULTS);
export const resetAddState = createAction(RESET_ADD_STATE);
export const setAuthorAddDefault = createAction(SET_AUTHOR_ADD_DEFAULT);
export const setBookAddDefault = createAction(SET_BOOK_ADD_DEFAULT);
export const updateSeriesEnrichment = createAction(UPDATE_SERIES_ENRICHMENT);

function getAjaxErrorMessage(xhr) {
  if (!xhr) {
    return 'Unknown error';
  }

  const responseJson = xhr.responseJSON;
  if (responseJson) {
    if (typeof responseJson === 'string') {
      return responseJson;
    }

    if (responseJson.message) {
      return responseJson.message;
    }

    if (Array.isArray(responseJson) && responseJson.length && responseJson[0].errorMessage) {
      return responseJson[0].errorMessage;
    }
  }

  const status = xhr.status ? `HTTP ${xhr.status}` : 'Request failed';
  const statusText = xhr.statusText || '';
  return `${status} ${statusText}`.trim();
}

function getMediaTypeLabel(mediaType) {
  return mediaType === 'ebook' ? 'eBook' : 'audiobook';
}

const pendingBookRequestMessage = 'The author and requested book are being prepared by the metadata server. Chaptarr saved your request and will add it automatically when it becomes available.';

function getPendingBookRequest(data, xhr) {
  const status = (xhr && xhr.status) || (data && data.pendingId ? 202 : null);

  if (status !== 202) {
    return null;
  }

  return {
    pendingId: (data && data.pendingId) || null,
    message: (data && data.message) || pendingBookRequestMessage
  };
}

function getQueuedBookActions(pendingRequest, extraState = {}) {
  return [
    showMessage({
      id: `book-request-queued-${Date.now()}`,
      name: 'BookRequestQueued',
      message: pendingRequest.message,
      type: 'success',
      hideAfter: 12
    }),
    set({
      section,
      isAdding: false,
      isAdded: false,
      isQueued: true,
      addNotice: pendingRequest.message,
      pendingId: pendingRequest.pendingId,
      addError: null,
      addFailedMediaType: null,
      ...extraState
    })
  ];
}

function getAddedMediaTypes(state, ...mediaTypes) {
  const searchState = getSectionState(state, section);
  const added = new Set(Array.isArray(searchState.addedMediaTypes) ? searchState.addedMediaTypes : []);

  mediaTypes.forEach((mediaType) => {
    if (mediaType === 'audiobook' || mediaType === 'ebook') {
      added.add(mediaType);
    }
  });

  return Array.from(added);
}

function getMediaTypePayload(payload, mediaType, searchForNewBook) {
  const isAudiobook = mediaType === 'audiobook';
  const requestedMonitor = isAudiobook ?
    (payload.audiobookMonitor || payload.monitor) :
    (payload.ebookMonitor || payload.monitor);
  const monitorValue = (requestedMonitor ?? '').toString().trim().toLowerCase();
  const normalizedRequestedMonitor = ['all', 'none'].includes(monitorValue) ? monitorValue : 'specificBook';

  return {
    ...payload,
    mediaType,
    searchForNewBook: !!searchForNewBook,
    monitor: normalizedRequestedMonitor,
    monitorNewItems: normalizeMonitorNewItemsOption(isAudiobook ?
      (payload.audiobookMonitorNewItems || payload.monitorNewItems) :
      (payload.ebookMonitorNewItems || payload.monitorNewItems)),
    metadataProfileId: isAudiobook ? (payload.audiobookMetadataProfileId || payload.metadataProfileId) : (payload.ebookMetadataProfileId || payload.metadataProfileId),
    tags: isAudiobook ? (payload.audiobookTags || payload.tags) : (payload.ebookTags || payload.tags)
  };
}

function postBookForMediaType(itemToAdd, payload, mediaType, searchForNewBook) {
  const mediaPayload = getMediaTypePayload(payload, mediaType, searchForNewBook);
  const newBook = getNewBook(_.cloneDeep(itemToAdd.book), mediaPayload, mediaType);

  return createAjaxRequest({
    url: '/book',
    method: 'POST',
    dataType: 'json',
    contentType: 'application/json',
    data: JSON.stringify(newBook)
  }).request;
}

function mergeLocalBook(localBooks, addedBook, mediaType) {
  const current = Array.isArray(localBooks) ? localBooks : [];

  if (!addedBook || addedBook.id == null) {
    return current;
  }

  const localBook = {
    ...addedBook,
    mediaType
  };

  const existingIndex = current.findIndex((book) => book.id === localBook.id);

  if (existingIndex >= 0) {
    return current.map((book, index) => (
      index === existingIndex ? { ...book, ...localBook } : book
    ));
  }

  return [
    ...current,
    localBook
  ];
}

function updateSearchItemWithAddedBook(itemToAdd, addedBook, mediaType) {
  const book = itemToAdd.book || {};
  const localKey = mediaType === 'ebook' ? 'localEbookBooks' : 'localAudiobookBooks';

  return {
    ...itemToAdd,
    book: {
      ...book,
      [localKey]: mergeLocalBook(book[localKey], addedBook, mediaType)
    }
  };
}

function getBookUpdateActions(itemToAdd, addedBook, mediaType) {
  const actions = [];

  if (addedBook?.author) {
    actions.push(updateItem({ section: 'authors', ...addedBook.author }));
  }

  if (addedBook?.id != null) {
    actions.push(updateItem({ section: 'books', ...addedBook }));
  }

  const updatedItem = updateSearchItemWithAddedBook(itemToAdd, addedBook, mediaType);

  if (updatedItem.id != null) {
    actions.push(updateItem({ section, ...updatedItem }));
  }

  return {
    actions,
    updatedItem
  };
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [GET_SEARCH_RESULTS]: function(getState, payload, dispatch) {
    const requestId = ++currentSearchRequestId;

    if (abortCurrentRequest) {
      abortCurrentRequest();
      abortCurrentRequest = null;
    }

    dispatch(set({ section, isFetching: true }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/search',
      data: {
        term: payload.term,
        provider: payload.provider || 'hardcover'
      }
    });

    abortCurrentRequest = abortRequest;

    request.done((data) => {
      if (requestId !== currentSearchRequestId) {
        return;
      }

      abortCurrentRequest = null;

      dispatch(batchActions([
        update({ section, data: orderAuthorSearchResults(data, payload.term) }),

        set({
          section,
          isFetching: false,
          isPopulated: true,
          error: null
        })
      ]));
    });

    request.fail((xhr) => {
      if (requestId !== currentSearchRequestId) {
        return;
      }

      abortCurrentRequest = null;

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });
  },

  [ADD_AUTHOR]: function(getState, payload, dispatch) {
    dispatch(set({ section, isAdding: true }));

    const foreignAuthorId = payload.foreignAuthorId;

    // Build V5 import payload based on selected settings
    const monitor = normalizeMonitorOption(payload.monitor);
    const monitorNewItems = normalizeMonitorNewItemsOption(payload.monitorNewItems);
    const audiobookRoot = payload.audiobookRootFolderPath;
    const ebookRoot = payload.ebookRootFolderPath;
    const audiobookProfile = payload.audiobookQualityProfileId;
    const ebookProfile = payload.ebookQualityProfileId;
    const metadataProfile = payload.metadataProfileId;
    const audiobookMeta = payload.audiobookMetadataProfileId;
    const ebookMeta = payload.ebookMetadataProfileId;

    // Prefer the explicit UI media type when provided; fall back to a heuristic for backward compatibility.
    const explicitMediaType = (payload.mediaType || '').toLowerCase();

    // Use the v1 import endpoint for authors
    const v1Url = `${window.location.origin}${window.Chaptarr.urlBase || ''}/api/v1/author/import`;
    const postImport = (v1Payload) => {
      return createAjaxRequest({
        url: v1Url,
        method: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        headers: { 'X-Api-Key': window.Chaptarr.apiKey },
        data: JSON.stringify(v1Payload)
      }).request;
    };

    let mediaType = explicitMediaType;
    if (!['audiobook', 'ebook', 'both'].includes(mediaType)) {
      mediaType = (audiobookRoot || audiobookProfile) ? 'audiobook' : 'ebook';
    }
    const requestedMediaTypes = mediaType === 'both' ? ['audiobook', 'ebook'] : [mediaType];
    const metadataByMediaType = { audiobook: audiobookMeta || metadataProfile, ebook: ebookMeta || metadataProfile };

    if (requestedMediaTypes.some((type) => !metadataByMediaType[type] || metadataByMediaType[type] <= 0)) {
      dispatch(batchActions([
        showMessage({
          id: `author-import-missing-metadataprofile-${Date.now()}`,
          name: 'AuthorImportMissingMetadataProfile',
          message: mediaType === 'both' ?
            translate('AuthorImportBothMetadataRequired') :
            translate('AuthorImportMetadataRequired'),
          type: 'error',
          hideAfter: 14
        }),
        set({ section, isAdding: false, isAdded: false, addError: null })
      ]));
      return;
    }

    // Submit both formats in one request through the same response path as a
    // single side. Per-format settings stay independent except linked policies.
    const v1Payload = {
      foreignAuthorId,
      authorName: payload.authorName,
      mediaType,
      syncMonitoredAcrossFormats: payload.syncMonitoredAcrossFormats,
      manualFlag: true,
      searchForMissingBooks: payload.searchForMissingBooks
    };
    for (const type of ['audiobook', 'ebook']) {
      // A linked new-book policy also applies to an already-configured side.
      if (payload[`${type}MonitorNewItems`] != null) {
        v1Payload[`${type}MonitorNewItems`] = normalizeMonitorNewItemsOption(payload[`${type}MonitorNewItems`]);
      }
      if (requestedMediaTypes.includes(type)) {
        const rootFolder = type === 'audiobook' ? audiobookRoot : ebookRoot;
        const qualityProfileId = type === 'audiobook' ? audiobookProfile : ebookProfile;
        const selectedMonitor = normalizeMonitorOption(payload[`${type}Monitor`] ?? monitor);
        v1Payload[`${type}MonitorExistingMode`] = selectedMonitor;
        v1Payload[`${type}Monitored`] = payload[`${type}Monitored`] !== false;
        v1Payload[`${type}MonitorNewItems`] = normalizeMonitorNewItemsOption(payload[`${type}MonitorNewItems`] ?? monitorNewItems);
        if (mediaType === 'both') {
          v1Payload[`${type}RootFolderPath`] = rootFolder || '';
          v1Payload[`${type}QualityProfileId`] = qualityProfileId || 0;
          v1Payload[`${type}MetadataProfileId`] = metadataByMediaType[type];
          v1Payload[`${type}Tags`] = payload[`${type}Tags`] ?? payload.tags;
        } else {
          Object.assign(v1Payload, {
            rootFolder: rootFolder || '',
            qualityProfileId: qualityProfileId || 0,
            metadataProfileId: metadataByMediaType[type],
            monitor: selectedMonitor,
            tags: payload[`${type}Tags`] ?? payload.tags
          });
        }
      }
    }

    const promise = postImport(v1Payload);

    promise.done((data, textStatus, xhr) => {
      const status = (xhr && xhr.status) || (data && data.pendingId ? 202 : 201);
      // 201 Created: author added immediately
      if (status === 201) {
        // Fetch the full author resource for UI state
        const authorId = data && data.id;
        const fetchReq = createAjaxRequest({
          url: `/author/${authorId}`,
          method: 'GET',
          dataType: 'json'
        }).request;

        fetchReq.done((authorData) => {
          dispatch(batchActions([
            updateItem({ section: 'authors', ...authorData }),
            showMessage({
              id: `author-add-${Date.now()}`,
              name: 'AuthorAdded',
              message: 'Author added to your library',
              type: 'success',
              hideAfter: 8
            }),
            set({
              section,
              isAdding: false,
              isAdded: true,
              isQueued: false,
              addNotice: null,
              pendingId: null,
              addError: null
            })
          ]));
        });

        fetchReq.fail(() => {
          // Even if fetch fails, mark as added with minimal state
          dispatch(batchActions([
            showMessage({
              id: `author-add-${Date.now()}`,
              name: 'AuthorAdded',
              message: 'Author added to your library',
              type: 'success',
              hideAfter: 8
            }),
            set({
              section,
              isAdding: false,
              isAdded: true,
              isQueued: false,
              addNotice: null,
              pendingId: null,
              addError: null
            })
          ]));
        });
        return;
      }

      // 202 Accepted: provider queued the author for import
      if (status === 202) {
        const message = (data && data.message) ? data.message : 'Author queued for import';
        const pendingId = (data && data.pendingId) || null;

        dispatch(batchActions([
          showMessage({
            id: `author-queued-${Date.now()}`,
            name: 'AuthorQueued',
            message,
            type: 'success',
            hideAfter: 10
          }),
          set({
            section,
            isAdding: false,
            isAdded: false,
            isQueued: true,
            addNotice: message,
            pendingId,
            addError: null
          })
        ]));
        return;
      }

      // Fallback for legacy backends returning 200 with created object
      dispatch(batchActions([
        updateItem({ section: 'authors', ...data }),
        showMessage({
          id: `author-add-${Date.now()}`,
          name: 'AuthorAdded',
          message: 'Author added to your library',
          type: 'success',
          hideAfter: 8
        }),
        data && data.hydrationWarning ? showMessage({
          id: `author-add-hydration-warning-${Date.now()}`,
          name: 'AuthorHydrationWarning',
          message: data.hydrationWarning,
          type: 'warning',
          hideAfter: 12
        }) : null,
        set({
          section,
          isAdding: false,
          isAdded: true,
          isQueued: false,
          addNotice: null,
          pendingId: null,
          addError: null
        })
      ].filter(Boolean)));
    });

    promise.fail((xhr) => {
      const error = getAjaxErrorMessage(xhr);

      dispatch(batchActions([
        showMessage({
          id: `author-add-failed-${Date.now()}`,
          name: 'AuthorAddFailed',
          message: `Author import failed: ${error}`,
          type: 'error',
          hideAfter: 14
        }),
        set({
          section,
          isAdding: false,
          isAdded: false,
          addError: xhr
        })
      ]));
    });
  },

  [ADD_BOOK]: function(getState, payload, dispatch) {
    dispatch(set({
      section,
      isAdding: true,
      isAdded: false,
      isQueued: false,
      addNotice: null,
      pendingId: null,
      addError: null,
      addFailedMediaType: null
    }));

    const foreignBookId = payload.foreignBookId;
    const items = getState().search.items;
    const itemToAdd = _.find(items, { foreignId: foreignBookId }) || (payload.book ? {
      foreignId: foreignBookId,
      book: payload.book
    } : null);

    if (!itemToAdd || !itemToAdd.book) {
      console.error('[searchActions] Cannot add book - item or book data missing', { foreignBookId, itemToAdd });
      dispatch(set({
        section,
        isAdding: false,
        isAdded: false,
        addFailedMediaType: null,
        addError: {
          message: 'Cannot add this book: missing search result data'
        }
      }));
      return;
    }

    const requestedMediaType = (payload.mediaType || '').toLowerCase();

    if (requestedMediaType === 'both') {
      const addEbook = (currentItem, audiobookError = null, audiobookPending = null) => {
        const second = postBookForMediaType(currentItem, payload, 'ebook', payload.searchForNewBook);

        second.done((ebookData, textStatus, xhr) => {
          const ebookPending = getPendingBookRequest(ebookData, xhr);
          const pendingRequest = ebookPending || audiobookPending;
          if (pendingRequest) {
            const ebookUpdate = ebookPending ? { actions: [] } : getBookUpdateActions(currentItem, ebookData, 'ebook');
            const audiobookFailureAction = audiobookError ? showMessage({
              id: `book-add-audiobook-failed-${Date.now()}`,
              name: 'BookAddAudiobookFailed',
              message: `The eBook request was queued, but audiobook failed: ${getAjaxErrorMessage(audiobookError)}. The modal is still open so you can retry audiobook.`,
              type: 'warning',
              hideAfter: 14
            }) : null;
            dispatch(batchActions([
              ...ebookUpdate.actions,
              audiobookFailureAction,
              ...getQueuedBookActions(pendingRequest, {
                addError: audiobookError,
                addFailedMediaType: audiobookError ? 'audiobook' : null,
                addedMediaTypes: getAddedMediaTypes(
                  getState(),
                  ...(audiobookPending ? [] : ['audiobook']),
                  ...(ebookPending ? [] : ['ebook'])
                )
              })
            ].filter(Boolean)));
            return;
          }

          const ebookUpdate = getBookUpdateActions(currentItem, ebookData, 'ebook');

          if (audiobookError) {
            const error = getAjaxErrorMessage(audiobookError);

            dispatch(batchActions([
              ...ebookUpdate.actions,
              showMessage({
                id: `book-add-audiobook-failed-${Date.now()}`,
                name: 'BookAddAudiobookFailed',
                message: `eBook added, but audiobook failed: ${error}. The modal is still open so you can retry audiobook.`,
                type: 'warning',
                hideAfter: 14
              }),
              set({
                section,
                isAdding: false,
                isAdded: false,
                addError: audiobookError,
                addFailedMediaType: 'audiobook',
                addedMediaTypes: getAddedMediaTypes(getState(), 'ebook')
              })
            ]));
            return;
          }

          dispatch(batchActions([
            ...ebookUpdate.actions,
            set({
              section,
              isAdding: false,
              isAdded: true,
              addError: null,
              addFailedMediaType: null,
              addedMediaTypes: getAddedMediaTypes(getState(), 'audiobook', 'ebook')
            })
          ]));
        });

        second.fail((ebookError) => {
          const error = getAjaxErrorMessage(ebookError);
          let audiobookMessage = 'Audiobook added, but ';
          let addedMediaTypes = getAddedMediaTypes(getState(), 'audiobook');

          if (audiobookPending) {
            audiobookMessage = 'Audiobook request queued, but ';
            addedMediaTypes = getAddedMediaTypes(getState());
          }

          if (audiobookError) {
            audiobookMessage = `Audiobook failed: ${getAjaxErrorMessage(audiobookError)}. `;
            addedMediaTypes = getAddedMediaTypes(getState());
          }

          dispatch(batchActions([
            showMessage({
              id: `book-add-ebook-failed-${Date.now()}`,
              name: 'BookAddEbookFailed',
              message: `${audiobookMessage}eBook failed: ${error}. The modal is still open so you can retry.`,
              type: audiobookError ? 'error' : 'warning',
              hideAfter: 14
            }),
            set({
              section,
              isAdding: false,
              isAdded: false,
              isQueued: !!audiobookPending,
              addNotice: audiobookPending ? audiobookPending.message : null,
              pendingId: audiobookPending ? audiobookPending.pendingId : null,
              addError: audiobookError || ebookError,
              addFailedMediaType: audiobookError ? 'audiobook' : 'ebook',
              addedMediaTypes
            })
          ]));
        });
      };

      const first = postBookForMediaType(itemToAdd, payload, 'audiobook', payload.searchForNewBook);

      first.done((audiobookData, textStatus, xhr) => {
        const audiobookPending = getPendingBookRequest(audiobookData, xhr);
        if (audiobookPending) {
          addEbook(itemToAdd, null, audiobookPending);
          return;
        }

        const audiobookUpdate = getBookUpdateActions(itemToAdd, audiobookData, 'audiobook');
        const currentItem = audiobookUpdate.updatedItem;

        dispatch(batchActions([
          ...audiobookUpdate.actions,
          set({
            section,
            addedMediaTypes: getAddedMediaTypes(getState(), 'audiobook')
          })
        ]));

        addEbook(currentItem);
      });

      first.fail((xhr) => {
        // Still attempt the eBook request. If the author was queued because metadata is
        // not available yet, this merges the second media type and its search intent
        // into the same pending author import.
        addEbook(itemToAdd, xhr);
      });

      return;
    }

    const mediaType = requestedMediaType === 'ebook' ? 'ebook' : 'audiobook';
    const promise = postBookForMediaType(itemToAdd, payload, mediaType, payload.searchForNewBook);

    promise.done((data, textStatus, xhr) => {
      const pendingRequest = getPendingBookRequest(data, xhr);
      if (pendingRequest) {
        dispatch(batchActions(getQueuedBookActions(pendingRequest)));
        return;
      }

      const updateResult = getBookUpdateActions(itemToAdd, data, mediaType);
      dispatch(batchActions([
        ...updateResult.actions,
        set({
          section,
          isAdding: false,
          isAdded: true,
          addError: null,
          addFailedMediaType: null,
          addedMediaTypes: getAddedMediaTypes(getState(), mediaType)
        })
      ]));
    });

    promise.fail((xhr) => {
      const error = getAjaxErrorMessage(xhr);

      dispatch(batchActions([
        showMessage({
          id: `book-add-${mediaType}-failed-${Date.now()}`,
          name: 'BookAddFailed',
          message: `${getMediaTypeLabel(mediaType)} failed: ${error}. The modal is still open so you can retry.`,
          type: 'error',
          hideAfter: 14
        }),
        set({
          section,
          isAdding: false,
          isAdded: false,
          addError: xhr,
          addFailedMediaType: mediaType
        })
      ]));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [RESET_ADD_STATE]: function(state) {
    const newState = getSectionState(state, section);

    newState.isAdding = false;
    newState.isAdded = false;
    newState.isQueued = false;
    newState.addNotice = null;
    newState.pendingId = null;
    newState.addedMediaTypes = [];
    newState.addFailedMediaType = null;
    newState.addError = null;

    return updateSectionState(state, section, newState);
  },

  [SET_AUTHOR_ADD_DEFAULT]: function(state, { payload }) {
    const newState = getSectionState(state, section);

    newState.authorDefaults = {
      ...newState.authorDefaults,
      ...payload
    };

    return updateSectionState(state, section, newState);
  },

  [SET_BOOK_ADD_DEFAULT]: function(state, { payload }) {
    const newState = getSectionState(state, section);

    newState.bookDefaults = {
      ...newState.bookDefaults,
      ...payload
    };

    return updateSectionState(state, section, newState);
  },

  [CLEAR_SEARCH_RESULTS]: function(state) {
    const {
      authorDefaults,
      bookDefaults,
      ...otherDefaultState
    } = defaultState;

    return Object.assign({}, state, otherDefaultState);
  },

  [UPDATE_SERIES_ENRICHMENT]: function(state, { payload }) {
    const { seriesId, series } = payload;
    const newState = getSectionState(state, section);

    // Find and update the series in the search results
    const updatedItems = newState.items.map((item) => {
      if (item.series && item.series.foreignSeriesId === seriesId) {
        // Update the series with enriched data
        return {
          ...item,
          series: {
            ...item.series,
            ...series,
            // Ensure images are properly updated
            images: series.images || item.series.images
          }
        };
      }
      return item;
    });

    return updateSectionState(state, section, {
      ...newState,
      items: updatedItems
    });
  }

}, defaultState, section);
