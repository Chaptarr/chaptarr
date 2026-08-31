import _ from 'lodash';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { filterTypePredicates, filterTypes, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import { getAuthorStatisticsForMediaType } from 'Utilities/Author/getAuthorStatisticsForMediaType';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import dateFilterPredicate from 'Utilities/Date/dateFilterPredicate';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import { showMessage } from './appActions';
import { set, update, updateItem } from './baseActions';
import { fetchBooks } from './bookActions';
import createHandleActions from './Creators/createHandleActions';
import createRemoveItemHandler from './Creators/createRemoveItemHandler';
import createSaveProviderHandler from './Creators/createSaveProviderHandler';
import createSetSettingValueReducer from './Creators/Reducers/createSetSettingValueReducer';

//
// Variables

export const section = 'authors';

export const filters = [
  {
    key: 'all',
    label: () => translate('All'),
    filters: []
  },
  {
    key: 'monitored',
    label: () => translate('Monitored'),
    filters: [
      {
        key: 'monitored',
        value: true,
        type: filterTypes.EQUAL
      }
    ]
  },
  {
    key: 'unmonitored',
    label: () => translate('Unmonitored'),
    filters: [
      {
        key: 'monitored',
        value: false,
        type: filterTypes.EQUAL
      }
    ]
  },
  {
    key: 'continuing',
    label: () => translate('ActiveOnly'),
    filters: [
      {
        key: 'status',
        value: 'continuing',
        type: filterTypes.EQUAL
      }
    ]
  },
  {
    key: 'ended',
    label: () => translate('DeadOnly'),
    filters: [
      {
        key: 'status',
        value: 'ended',
        type: filterTypes.EQUAL
      }
    ]
  },
  {
    key: 'missing',
    label: () => translate('MissingBooks'),
    filters: [
      {
        key: 'missing',
        value: true,
        type: filterTypes.EQUAL
      }
    ]
  }
];

function getAuthorMediaValues(item, state, audiobookKey, ebookKey) {
  if (state?.selectedMediaType === 'audiobook') {
    return [item[audiobookKey]];
  }

  if (state?.selectedMediaType === 'ebook') {
    return [item[ebookKey]];
  }

  return [item[audiobookKey], item[ebookKey]];
}

function matchesAuthorMediaValue(item, filterValue, type, state, audiobookKey, ebookKey) {
  const predicate = filterTypePredicates[type];
  const values = getAuthorMediaValues(item, state, audiobookKey, ebookKey)
    .filter((value) => value != null);
  const isNegative = type === filterTypes.NOT_EQUAL ||
    type === filterTypes.NOT_CONTAINS ||
    type === filterTypes.NOT_STARTS_WITH ||
    type === filterTypes.NOT_ENDS_WITH;

  return isNegative ?
    values.every((value) => predicate(value, filterValue)) :
    values.some((value) => predicate(value, filterValue));
}

function getAuthorMediaValue(item, selectedMediaType, audiobookKey, ebookKey) {
  const mediaType = selectedMediaType === 'audiobook' || selectedMediaType === 'ebook' ?
    selectedMediaType :
    item.lastSelectedMediaType;

  return mediaType === 'ebook' ? item[ebookKey] : item[audiobookKey];
}

export const filterPredicates = {
  missing: function(item, filterValue, type, state) {
    const mediaStatistics = getAuthorStatisticsForMediaType(item, state?.selectedMediaType);

    return (mediaStatistics.bookCount || 0) > (mediaStatistics.availableBookCount || 0);
  },

  nextBook: function(item, filterValue, type) {
    return dateFilterPredicate(item.nextBook, filterValue, type);
  },

  lastBook: function(item, filterValue, type) {
    return dateFilterPredicate(item.lastBook, filterValue, type);
  },

  added: function(item, filterValue, type) {
    return dateFilterPredicate(item.added, filterValue, type);
  },

  ratings: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];

    return predicate(item.ratings.value * 10, filterValue);
  },

  bookCount: function(item, filterValue, type, state) {
    const predicate = filterTypePredicates[type];
    const statistics = getAuthorStatisticsForMediaType(item, state?.selectedMediaType);
    const bookCount = statistics.bookCount || 0;

    return predicate(bookCount, filterValue);
  },

  sizeOnDisk: function(item, filterValue, type, state) {
    const predicate = filterTypePredicates[type];
    const statistics = getAuthorStatisticsForMediaType(item, state?.selectedMediaType);
    const sizeOnDisk = statistics.sizeOnDisk || 0;

    return predicate(sizeOnDisk, filterValue);
  },

  qualityProfileId: function(item, filterValue, type, state) {
    return matchesAuthorMediaValue(item, filterValue, type, state, 'audiobookQualityProfileId', 'ebookQualityProfileId');
  },

  metadataProfileId: function(item, filterValue, type, state) {
    return matchesAuthorMediaValue(item, filterValue, type, state, 'audiobookMetadataProfileId', 'ebookMetadataProfileId');
  },

  path: function(item, filterValue, type, state) {
    return matchesAuthorMediaValue(item, filterValue, type, state, 'audiobookFolder', 'ebookFolder');
  },

  rootFolderPath: function(item, filterValue, type, state) {
    return matchesAuthorMediaValue(item, filterValue, type, state, 'audiobookRootFolderPath', 'ebookRootFolderPath');
  },

  tags: function(item, filterValue, type, state) {
    const predicate = filterTypePredicates[type];
    const tags = getAuthorMediaValues(item, state, 'audiobookTags', 'ebookTags')
      .flatMap((value) => value || []);

    return predicate(tags, filterValue);
  }
};

export const sortPredicates = {
  status: function(item) {
    // Status sorting is life-status only.
    return item.status === 'continuing' ? 1 : 0;
  },

  sizeOnDisk: function(item, _direction, selectedMediaType) {
    const statistics = getAuthorStatisticsForMediaType(item, selectedMediaType);

    return statistics.sizeOnDisk || 0;
  },

  qualityProfileId: function(item, _direction, selectedMediaType) {
    return getAuthorMediaValue(item, selectedMediaType, 'audiobookQualityProfileId', 'ebookQualityProfileId') || 0;
  },

  metadataProfileId: function(item, _direction, selectedMediaType) {
    return getAuthorMediaValue(item, selectedMediaType, 'audiobookMetadataProfileId', 'ebookMetadataProfileId') || 0;
  },

  path: function(item, _direction, selectedMediaType) {
    return getAuthorMediaValue(item, selectedMediaType, 'audiobookFolder', 'ebookFolder') || '';
  }
};

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  isSaving: false,
  saveError: null,
  isLinking: false,
  items: [],
  sortKey: 'sortName',
  sortDirection: sortDirections.ASCENDING,
  pendingChanges: {}
};

//
// Actions Types

export const FETCH_AUTHOR = 'authors/fetchAuthor';
export const SET_AUTHOR_VALUE = 'authors/setAuthorValue';
export const SAVE_AUTHOR = 'authors/saveAuthor';
export const DELETE_AUTHOR = 'authors/deleteAuthor';

export const TOGGLE_AUTHOR_MONITORED = 'authors/toggleAuthorMonitored';
export const TOGGLE_BOOK_MONITORED = 'authors/toggleBookMonitored';
export const UPDATE_BOOK_MONITORED = 'authors/updateBookMonitored';
export const UPDATE_AUTHOR_MEDIA_TYPE = 'authors/updateAuthorMediaType';
export const LINK_AUTHOR_TO_FOLDER = 'authors/linkAuthorToFolder';
export const FETCH_AUTHOR_MEDIA_TYPE_SIZE = 'authors/fetchAuthorMediaTypeSize';
export const SET_AUTHOR_MEDIA_TYPE_SIZE = 'authors/setAuthorMediaTypeSize';

//
// Action Creators

export const fetchAuthor = createThunk(FETCH_AUTHOR);
export const saveAuthor = createThunk(SAVE_AUTHOR, (payload) => {
  const newPayload = {
    ...payload
  };

  if (payload.moveFiles) {
    newPayload.queryParams = {
      moveFiles: true
    };
  }

  delete newPayload.moveFiles;

  return newPayload;
});

export const deleteAuthor = createThunk(DELETE_AUTHOR, (payload) => {
  return {
    ...payload,
    queryParams: {
      deleteFiles: payload.deleteFiles,
      addImportListExclusion: payload.addImportListExclusion,
      readdAuthor: payload.readdAuthor
    }
  };
});

export const toggleAuthorMonitored = createThunk(TOGGLE_AUTHOR_MONITORED);
export const toggleBookMonitored = createThunk(TOGGLE_BOOK_MONITORED);
export const updateBookMonitor = createThunk(UPDATE_BOOK_MONITORED);
export const updateAuthorMediaType = createThunk(UPDATE_AUTHOR_MEDIA_TYPE);
export const linkAuthorToFolder = createThunk(LINK_AUTHOR_TO_FOLDER);
export const fetchAuthorMediaTypeSize = createThunk(FETCH_AUTHOR_MEDIA_TYPE_SIZE);

export const setAuthorValue = createAction(SET_AUTHOR_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Helpers

function getSaveAjaxOptions({ ajaxOptions, payload }) {
  if (payload.moveFolder) {
    ajaxOptions.url = `${ajaxOptions.url}?moveFolder=true`;
  }

  return ajaxOptions;
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [FETCH_AUTHOR]: function(getState, payload, dispatch) {
    dispatch(set({ section, isFetching: true }));

    const { id, ...otherPayload } = payload;

    // Always use numeric id
    const url = id != null ? `/author/${id}` : '/author';

    const { request, abortRequest } = createAjaxRequest({
      url,
      data: otherPayload,
      traditional: true
    });

    request.done((data) => {
      dispatch(batchActions([
        (id == null) ? update({ section, data }) : updateItem({ section, ...data }),
        set({
          section,
          isFetching: false,
          isPopulated: true,
          error: null
        })
      ]));
    });

    request.fail((xhr) => {
      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });

    return abortRequest;
  },
  [SAVE_AUTHOR]: createSaveProviderHandler(section, '/author', { getAjaxOptions: getSaveAjaxOptions }),
  [DELETE_AUTHOR]: createRemoveItemHandler(section, '/author'),

  [TOGGLE_AUTHOR_MONITORED]: (getState, payload, dispatch) => {
    const {
      authorId: id,
      monitored,
      mediaType
    } = payload;

    const author = _.find(getState().authors.items, { id });

    if (!author) {
      return;
    }

    const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, mediaType);
    const effectiveMediaType = rootFolderStatus.mediaType;

    if (!rootFolderStatus.hasRootFolder) {
      dispatch(showMessage({
        id: `author-monitor-toggle-no-root-folder-${id}-${effectiveMediaType}`,
        name: 'AuthorMonitorToggleNoRootFolder',
        message: `No ${effectiveMediaType} root folder configured for this author`,
        type: 'warning',
        hideAfter: 10
      }));

      return;
    }

    dispatch(updateItem({
      id,
      section,
      isSaving: true
    }));

    // Update only the author-level gate for the selected media type. Book-row
    // monitoring remains independent and is never changed by this control.
    const updateData = { ...author };

    // Remove read-only nested objects; they can include LazyLoaded<> metadata that isn't valid to send back on PUT.
    delete updateData.nextBook;
    delete updateData.lastBook;

    if (effectiveMediaType === 'audiobook') {
      updateData.audiobookMonitored = monitored === true;
      updateData.audiobookSettingsManuallyOverridden = true; // Mark as manually set
    } else if (effectiveMediaType === 'ebook') {
      updateData.ebookMonitored = monitored === true;
      updateData.ebookSettingsManuallyOverridden = true; // Mark as manually set
    }

    // Handle MetadataProfileId - if it's 0, undefined, or null, remove it entirely
    // The backend validation rejects 0 but accepts missing/null
    if (!updateData.metadataProfileId || updateData.metadataProfileId === 0) {
      delete updateData.metadataProfileId;
    }

    const promise = createAjaxRequest({
      url: `/author/${id}`,
      method: 'PUT',
      data: JSON.stringify(updateData),
      dataType: 'json'
    }).request;

    promise.done((data) => {
      // CONTEXT-AWARE MONITORING: Update Redux state with the correct media-type-specific field
      const stateUpdate = {
        id,
        section,
        isSaving: false
      };

      // Only update fields that are actually returned from the server
      // Don't overwrite images if they're not in the response
      if (data) {
        // Preserve images if not included in response
        if (!data.images && author.images) {
          stateUpdate.images = author.images;
        }
        // Merge the rest of the data
        Object.assign(stateUpdate, data);
      }

      // Ensure the specific media type monitoring field is updated in Redux state
      if (effectiveMediaType === 'audiobook') {
        stateUpdate.audiobookMonitored = monitored === true;
        stateUpdate.audiobookSettingsManuallyOverridden = true;
      } else if (effectiveMediaType === 'ebook') {
        stateUpdate.ebookMonitored = monitored === true;
        stateUpdate.ebookSettingsManuallyOverridden = true;
      }

      dispatch(updateItem(stateUpdate));
    });

    promise.fail((xhr) => {
      dispatch(showMessage({
        id: `author-save-failed-${id}-${Date.now()}`,
        name: 'AuthorSaveFailed',
        message: getErrorMessage(xhr, 'Unable to save author settings'),
        type: 'error',
        hideAfter: 10
      }));

      dispatch(updateItem({
        id,
        section,
        isSaving: false
      }));
    });
  },

  [TOGGLE_BOOK_MONITORED]: function(getState, payload, dispatch) {
    const {
      authorId: id,
      seasonNumber,
      monitored
    } = payload;

    const author = _.find(getState().authors.items, { id });
    const seasons = _.cloneDeep(author.seasons);
    const season = _.find(seasons, { seasonNumber });

    season.isSaving = true;

    dispatch(updateItem({
      id,
      section,
      seasons
    }));

    season.monitored = monitored;

    const promise = createAjaxRequest({
      url: `/author/${id}`,
      method: 'PUT',
      data: JSON.stringify({
        ...author,
        seasons
      }),
      dataType: 'json'
    }).request;

    promise.done((data) => {
      const books = _.filter(getState().books.items, { authorId: id, seasonNumber });

      dispatch(batchActions([
        updateItem({
          id,
          section,
          ...data
        }),

        ...books.map((book) => {
          return updateItem({
            id: book.id,
            section: 'books',
            monitored
          });
        })
      ]));
    });

    promise.fail((xhr) => {
      dispatch(updateItem({
        id,
        section,
        seasons: author.seasons
      }));
    });
  },

  [UPDATE_BOOK_MONITORED]: function(getState, payload, dispatch) {
    const {
      id,
      monitor,
      mediaType
    } = payload;

    dispatch(set({
      section,
      isSaving: true
    }));

    const promise = createAjaxRequest({
      url: '/bookshelf',
      method: 'POST',
      data: JSON.stringify({
        authors: [{ id }],
        monitoringOptions: { monitor, mediaType }
      }),
      dataType: 'json'
    }).request;

    promise.done((data) => {
      const { app = {} } = getState();
      const selectedMediaType = mediaType || app.selectedMediaType || 'audiobook';
      const hideUnmonitoredMissing = app.hideUnmonitoredMissing;

      const fetchParams = {
        authorId: id,
        mediaType: selectedMediaType
      };

      if (hideUnmonitoredMissing) {
        fetchParams.monitored = true;
      }

      dispatch(fetchBooks(fetchParams));

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
  },

  [UPDATE_AUTHOR_MEDIA_TYPE]: function(getState, payload, dispatch) {
    const {
      authorId,
      mediaType
    } = payload;

    dispatch(updateItem({
      id: authorId,
      section,
      lastSelectedMediaType: mediaType
    }));

    const promise = createAjaxRequest({
      url: `/author/${authorId}/selectedMediaType/${mediaType}`,
      method: 'PUT'
    }).request;

    promise.done(() => {
      // Success - media type updated
    });

    promise.fail((xhr) => {
      // Revert on failure
      const author = _.find(getState().authors.items, { id: authorId });
      dispatch(updateItem({
        id: authorId,
        section,
        lastSelectedMediaType: author.lastSelectedMediaType
      }));
    });
  },

  [LINK_AUTHOR_TO_FOLDER]: (getState, payload, dispatch) => {
    const {
      authorId,
      rootFolderId,
      folderPath
    } = payload;

    dispatch(updateItem({
      id: authorId,
      section,
      isLinking: true
    }));

    const promise = createAjaxRequest({
      url: `/rootfolder/${rootFolderId}/link-author`,
      method: 'POST',
      data: JSON.stringify({
        authorId,
        folderPath
      })
    }).request;

    promise.done((data) => {
      dispatch(updateItem({
        id: authorId,
        section,
        isLinking: false,
        ...data
      }));
    });

    promise.fail((xhr) => {
      dispatch(updateItem({
        id: authorId,
        section,
        isLinking: false
      }));
    });
  },

  [FETCH_AUTHOR_MEDIA_TYPE_SIZE]: (getState, payload, dispatch) => {
    const {
      authorId,
      mediaType
    } = payload;

    const promise = createAjaxRequest({
      url: `/author/${authorId}/size/${mediaType}`,
      method: 'GET'
    }).request;

    promise.done((data) => {
      dispatch(updateItem({
        id: authorId,
        section,
        [`${mediaType}SizeOnDisk`]: data
      }));
    });

    promise.fail((xhr) => {
      console.error(`Failed to fetch ${mediaType} size for author ${authorId}:`, xhr);
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_AUTHOR_VALUE]: createSetSettingValueReducer(section)

}, defaultState, section);
