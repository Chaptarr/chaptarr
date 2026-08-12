import cloneDeep from 'lodash/cloneDeep';
import find from 'lodash/find';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import monitorNewItemsOptions from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions from 'Utilities/Author/monitorOptions';
import getNewBook from 'Utilities/Book/getNewBook';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import updateSectionState from 'Utilities/State/updateSectionState';
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
    monitor: monitorOptions[0].key,
    monitorNewItems: monitorNewItemsOptions[0].key,
    audiobookQualityProfileId: 0,
    ebookQualityProfileId: 0,
    audiobookMetadataProfileId: 0,
    ebookMetadataProfileId: 0,
    metadataProfileId: 0,
    tags: []
  },

  bookDefaults: {
    rootFolderPath: '',
    audiobookRootFolderPath: '',
    ebookRootFolderPath: '',
    monitor: monitorOptions[0].key,
    audiobookMonitor: monitorOptions[0].key,
    ebookMonitor: monitorOptions[0].key,
    monitorNewItems: monitorNewItemsOptions[0].key,
    audiobookMonitorNewItems: monitorNewItemsOptions[0].key,
    ebookMonitorNewItems: monitorNewItemsOptions[0].key,
    qualityProfileId: 1,
    audiobookQualityProfileId: 1,
    ebookQualityProfileId: 1,
    metadataProfileId: 0,
    audiobookMetadataProfileId: 0,
    ebookMetadataProfileId: 0,
    tags: [],
    audiobookTags: [],
    ebookTags: []
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

  return {
    ...payload,
    mediaType,
    searchForNewBook: !!searchForNewBook,
    monitor: isAudiobook ? (payload.audiobookMonitor || payload.monitor) : (payload.ebookMonitor || payload.monitor),
    monitorNewItems: isAudiobook ? (payload.audiobookMonitorNewItems || payload.monitorNewItems) : (payload.ebookMonitorNewItems || payload.monitorNewItems),
    metadataProfileId: isAudiobook ? (payload.audiobookMetadataProfileId || payload.metadataProfileId) : (payload.ebookMetadataProfileId || payload.metadataProfileId),
    tags: isAudiobook ? (payload.audiobookTags || payload.tags) : (payload.ebookTags || payload.tags)
  };
}

function postBookForMediaType(itemToAdd, payload, mediaType, searchForNewBook) {
  const mediaPayload = getMediaTypePayload(payload, mediaType, searchForNewBook);
  const newBook = getNewBook(cloneDeep(itemToAdd.book), mediaPayload, mediaType);

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
        update({ section, data }),

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
    const monitor = payload.monitor;
    const monitorNewItems = payload.monitorNewItems;
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

    const monitorExistingFromUi = (uiValue) => {
      if (uiValue === 'all') {
        return 'All';
      }

      if (uiValue === 'specificBook' || uiValue === 'select') {
        return 'Select';
      }

      return 'None';
    };

    const monitorFutureFromUi = (uiValue) => uiValue === 'all';

    const getImportErrorMessage = (xhr) => {
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
    };

    // Support a "Both" UI option by submitting two sequential imports (audiobook then ebook).
    // Backend expects single mediaType per request.
    if (explicitMediaType === 'both') {
      const audiobookMonitor = payload.audiobookMonitor || monitor;
      const ebookMonitor = payload.ebookMonitor || monitor;
      const audiobookMonitorNew = payload.audiobookMonitorNewItems || monitorNewItems;
      const ebookMonitorNew = payload.ebookMonitorNewItems || monitorNewItems;

      const audiobookMetadataProfileId = audiobookMeta || metadataProfile;
      const ebookMetadataProfileId = ebookMeta || metadataProfile;

      if (!audiobookMetadataProfileId || audiobookMetadataProfileId <= 0 || !ebookMetadataProfileId || ebookMetadataProfileId <= 0) {
        dispatch(batchActions([
          showMessage({
            id: `author-import-missing-metadataprofile-${Date.now()}`,
            name: 'AuthorImportMissingMetadataProfile',
            message: 'Select a metadata profile for both Audiobooks and Ebooks before adding this author.',
            type: 'error',
            hideAfter: 14
          }),
          set({
            section,
            isAdding: false,
            isAdded: false,
            addError: null
          })
        ]));

        return;
      }

      const audiobookV1Payload = {
        foreignAuthorId,
        mediaType: 'audiobook',
        rootFolder: audiobookRoot || '',
        qualityProfileId: audiobookProfile || 0,
        metadataProfileId: audiobookMetadataProfileId,
        monitorExisting: monitorExistingFromUi(audiobookMonitor),
        monitorFuture: monitorFutureFromUi(audiobookMonitorNew),
        manualFlag: true,
        // Defer missing search until the second import so both media types are hydrated first.
        searchForMissingBooks: false
      };

      const ebookV1Payload = {
        foreignAuthorId,
        mediaType: 'ebook',
        rootFolder: ebookRoot || '',
        qualityProfileId: ebookProfile || 0,
        metadataProfileId: ebookMetadataProfileId,
        monitorExisting: monitorExistingFromUi(ebookMonitor),
        monitorFuture: monitorFutureFromUi(ebookMonitorNew),
        manualFlag: true,
        searchForMissingBooks: payload.searchForMissingBooks
      };

      const first = postImport(audiobookV1Payload);

      first.done((data1, textStatus1, xhr1) => {
        const status1 = (xhr1 && xhr1.status) || (data1 && data1.pendingId ? 202 : 201);

        if ((status1 === 200 || status1 === 201) && data1 && data1.id) {
          dispatch(updateItem({ section: 'authors', ...data1 }));

          if (data1.hydrationWarning) {
            dispatch(showMessage({
              id: `author-add-hydration-warning-${Date.now()}`,
              name: 'AuthorHydrationWarning',
              message: data1.hydrationWarning,
              type: 'warning',
              hideAfter: 12
            }));
          }
        }

        const second = postImport(ebookV1Payload);

        second.done((data2, textStatus2, xhr2) => {
          const status2 = (xhr2 && xhr2.status) || (data2 && data2.pendingId ? 202 : 201);

          // If either request queued the import, treat the overall add as queued.
          if (status1 === 202 || status2 === 202) {
            const pendingId = (status2 === 202 && data2 && data2.pendingId) || (status1 === 202 && data1 && data1.pendingId) || null;

            let message = (data2 && data2.message) || (data1 && data1.message) || 'Author queued for import';
            if (status1 !== 202 && status2 === 202) {
              message = `Audiobooks added, eBooks queued for import. ${message}`.trim();
            } else if (status1 === 202 && status2 !== 202) {
              message = `eBooks added, audiobooks queued for import. ${message}`.trim();
            }

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

          // Prefer the second response (it should reflect the final merged settings)
          const authorId = (data2 && data2.id) || (data1 && data1.id);

          // 200 OK: we should already have the author resource
          if (status2 === 200 && data2) {
            dispatch(batchActions([
              updateItem({ section: 'authors', ...data2 }),
              showMessage({
                id: `author-add-${Date.now()}`,
                name: 'AuthorAdded',
                message: 'Author added to your library',
                type: 'success',
                hideAfter: 8
              }),
              data2.hydrationWarning ? showMessage({
                id: `author-add-hydration-warning-${Date.now()}`,
                name: 'AuthorHydrationWarning',
                message: data2.hydrationWarning,
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
            return;
          }

          // 201 Created: fetch the full author resource for UI state
          if (status2 === 201 && authorId) {
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

          // Fallback: treat as added without a fetch
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

        second.fail((xhr) => {
          const error = getImportErrorMessage(xhr);

          if (status1 === 202) {
            const message = (data1 && data1.message) ? data1.message : 'Author queued for import';
            const pendingId = (data1 && data1.pendingId) || null;

            dispatch(batchActions([
              showMessage({
                id: `author-queued-${Date.now()}`,
                name: 'AuthorQueued',
                message,
                type: 'success',
                hideAfter: 10
              }),
              showMessage({
                id: `author-add-ebook-failed-${Date.now()}`,
                name: 'AuthorAddEbookFailed',
                message: `Ebook import failed: ${error} (audiobook import is queued; you can retry eBooks later)`,
                type: 'error',
                hideAfter: 14
              }),
              set({
                section,
                isAdding: false,
                isAdded: false,
                isQueued: true,
                addNotice: message,
                pendingId,
                addError: xhr
              })
            ]));
            return;
          }

          dispatch(batchActions([
            showMessage({
              id: `author-add-audiobook-partial-${Date.now()}`,
              name: 'AuthorAddAudiobookPartial',
              message: 'Audiobooks added successfully, but eBooks failed to import.',
              type: 'warning',
              hideAfter: 12
            }),
            showMessage({
              id: `author-add-ebook-failed-${Date.now()}`,
              name: 'AuthorAddEbookFailed',
              message: `Ebook import failed: ${error} (you can retry by selecting Ebooks and clicking Add)`,
              type: 'error',
              hideAfter: 14
            }),
            set({
              section,
              isAdding: false,
              isAdded: false,
              isQueued: false,
              addNotice: null,
              pendingId: null,
              addError: xhr
            })
          ]));
        });
      });

      first.fail((xhr) => {
        const error = getImportErrorMessage(xhr);

        dispatch(batchActions([
          showMessage({
            id: `author-add-audiobook-failed-${Date.now()}`,
            name: 'AuthorAddAudiobookFailed',
            message: `Audiobook import failed: ${error}`,
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

      return;
    }

    let mediaType = explicitMediaType;
    if (explicitMediaType !== 'audiobook' && explicitMediaType !== 'ebook') {
      mediaType = (audiobookRoot || audiobookProfile) ? 'audiobook' : 'ebook';
    }

    const rootFolder = mediaType === 'audiobook' ? audiobookRoot : ebookRoot;
    const qualityProfileId = mediaType === 'audiobook' ? (audiobookProfile || 0) : (ebookProfile || 0);
    // Choose the correct per-type metadata profile ID for v5.
    // Do not hard-code IDs; require the UI-provided defaults or a generic metadataProfileId.
    const metadataProfileId = mediaType === 'audiobook' ?
      (audiobookMeta || metadataProfile) :
      (ebookMeta || metadataProfile);

    if (!metadataProfileId || metadataProfileId <= 0) {
      dispatch(batchActions([
        showMessage({
          id: `author-import-missing-metadataprofile-${Date.now()}`,
          name: 'AuthorImportMissingMetadataProfile',
          message: 'Select a metadata profile before adding this author.',
          type: 'error',
          hideAfter: 14
        }),
        set({
          section,
          isAdding: false,
          isAdded: false,
          addError: null
        })
      ]));

      return;
    }

    const monitorExisting = monitorExistingFromUi(monitor);
    const monitorFuture = monitorFutureFromUi(monitorNewItems);

    const v1Payload = {
      foreignAuthorId,
      mediaType,
      rootFolder: rootFolder || '',
      qualityProfileId,
      metadataProfileId,
      monitorExisting,
      monitorFuture,
      manualFlag: true,
      searchForMissingBooks: payload.searchForMissingBooks
    };

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
      const error = getImportErrorMessage(xhr);

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
      addError: null,
      addFailedMediaType: null
    }));

    const foreignBookId = payload.foreignBookId;
    const items = getState().search.items;
    const itemToAdd = find(items, { foreignId: foreignBookId }) || (payload.book ? {
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
      const first = postBookForMediaType(itemToAdd, payload, 'audiobook', false);

      first.done((audiobookData) => {
        const audiobookUpdate = getBookUpdateActions(itemToAdd, audiobookData, 'audiobook');
        const currentItem = audiobookUpdate.updatedItem;

        dispatch(batchActions([
          ...audiobookUpdate.actions,
          set({
            section,
            addedMediaTypes: getAddedMediaTypes(getState(), 'audiobook')
          })
        ]));

        const second = postBookForMediaType(currentItem, payload, 'ebook', payload.searchForNewBook);

        second.done((ebookData) => {
          const ebookUpdate = getBookUpdateActions(currentItem, ebookData, 'ebook');

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

        second.fail((xhr) => {
          const error = getAjaxErrorMessage(xhr);

          dispatch(batchActions([
            showMessage({
              id: `book-add-ebook-failed-${Date.now()}`,
              name: 'BookAddEbookFailed',
              message: `Audiobook added, but eBook failed: ${error}. The modal is still open so you can retry eBook.`,
              type: 'warning',
              hideAfter: 14
            }),
            set({
              section,
              isAdding: false,
              isAdded: false,
              addError: xhr,
              addFailedMediaType: 'ebook',
              addedMediaTypes: getAddedMediaTypes(getState(), 'audiobook')
            })
          ]));
        });
      });

      first.fail((xhr) => {
        const error = getAjaxErrorMessage(xhr);

        dispatch(batchActions([
          showMessage({
            id: `book-add-audiobook-failed-${Date.now()}`,
            name: 'BookAddAudiobookFailed',
            message: `Audiobook failed: ${error}. The modal is still open so you can retry.`,
            type: 'error',
            hideAfter: 14
          }),
          set({
            section,
            isAdding: false,
            isAdded: false,
            addError: xhr,
            addFailedMediaType: 'audiobook'
          })
        ]));
      });

      return;
    }

    const mediaType = requestedMediaType === 'ebook' ? 'ebook' : 'audiobook';
    const promise = postBookForMediaType(itemToAdd, payload, mediaType, payload.searchForNewBook);

    promise.done((data) => {
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
