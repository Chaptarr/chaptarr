import _ from 'lodash';
import moment from 'moment';
import React from 'react';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import bookEntities from 'Book/bookEntities';
import Icon from 'Components/Icon';
import { filterTypePredicates, filterTypes, icons, sortDirections } from 'Helpers/Props';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import dateFilterPredicate from 'Utilities/Date/dateFilterPredicate';
import coerceMonitoredBoolean from 'Utilities/Monitoring/coerceMonitoredBoolean';
import translate from 'Utilities/String/translate';
import { removeItem, set, update, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createRemoveItemHandler from './Creators/createRemoveItemHandler';
import createSaveProviderHandler from './Creators/createSaveProviderHandler';
import createSetClientSideCollectionSortReducer from './Creators/Reducers/createSetClientSideCollectionSortReducer';
import createSetSettingValueReducer from './Creators/Reducers/createSetSettingValueReducer';
import createSetTableOptionReducer from './Creators/Reducers/createSetTableOptionReducer';

//
// Variables

export const section = 'books';
let abortCurrentFetchRequest = null;
let currentFetchRequestId = 0;

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
    key: 'downloaded',
    label: () => translate('Exists'),
    filters: [
      {
        key: 'bookFileCount',
        value: 0,
        type: filterTypes.GREATER_THAN
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
    key: 'missing',
    label: () => translate('Missing'),
    filters: [
      {
        key: 'monitored',
        value: true,
        type: filterTypes.EQUAL
      },
      {
        key: 'missing',
        value: true,
        type: filterTypes.EQUAL
      }
    ]
  },
  {
    key: 'wanted',
    label: () => translate('Wanted'),
    filters: [
      {
        key: 'monitored',
        value: true,
        type: filterTypes.EQUAL
      },
      {
        key: 'missing',
        value: true,
        type: filterTypes.EQUAL
      },
      {
        key: 'releaseDate',
        value: moment(),
        type: filterTypes.LESS_THAN
      }
    ]
  }
];

export const filterPredicates = {
  missing: function(item) {
    const { statistics = {} } = item;

    return !statistics.hasOwnProperty('bookFileCount') || statistics.bookFileCount === 0;
  },

  releaseDate: function(item, filterValue, type) {
    return dateFilterPredicate(item.releaseDate, filterValue, type);
  },

  added: function(item, filterValue, type) {
    return dateFilterPredicate(item.added, filterValue, type);
  },

  qualityProfileId: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];

    // Determine which quality profile to use based on lastSelectedMediaType
    const qualityProfileId = item.author.lastSelectedMediaType === 'ebook' 
      ? item.author.ebookQualityProfileId 
      : item.author.audiobookQualityProfileId;

    return predicate(qualityProfileId, filterValue);
  },

  ratings: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];

    return predicate(item.ratings.value * 10, filterValue);
  },

  path: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];

    return predicate(item.author.path, filterValue);
  },

  bookFileCount: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];
    const bookCount = item.statistics ? item.statistics.bookFileCount : 0;

    return predicate(bookCount, filterValue);
  },

  sizeOnDisk: function(item, filterValue, type) {
    const predicate = filterTypePredicates[type];
    const sizeOnDisk = item.statistics && item.statistics.sizeOnDisk ?
      item.statistics.sizeOnDisk :
      0;

    return predicate(sizeOnDisk, filterValue);
  }
};

export const sortPredicates = {
  title: function(item) {
    const title = item.title || '';
    return title.toLowerCase();
  },

  monitored: function(item, _direction, selectedMediaType) {
    const mediaType = (selectedMediaType || item.mediaType || '').toString().toLowerCase() === 'ebook' ? 'ebook' : 'audiobook';
    const isMonitored = mediaType === 'ebook' ? item.ebookMonitored : item.audiobookMonitored;

    return isMonitored ? 1 : 0;
  },

  sizeOnDisk: function(item) {
    const { statistics = {} } = item;

    return statistics.sizeOnDisk || 0;
  },

  path: function(item, direction) {
    const path = (item.author && item.author.path ? item.author.path : '').toString().toLowerCase();

    // Keep missing paths at the end regardless of direction
    if (!path) {
      return direction === sortDirections.ASCENDING ? '\uffff' : '';
    }

    return path;
  },

  series: function(item, direction) {
    const seriesTitle = (item.seriesTitle || '').toString().trim().toLowerCase();

    // Keep missing series titles at the end regardless of direction
    if (!seriesTitle) {
      return direction === sortDirections.ASCENDING ? '\uffff' : '';
    }

    return seriesTitle;
  },

  position: function(item) {
    const position = item.position;

    if (position == null) {
      return '';
    }

    // Pad zeros in position so sorting works as expected (e.g. 1, 2, 10)
    return position.replace(/(\d+)/g, (d) => {
      return d.padStart(10, '0');
    });
  },

  narrator: function(item, direction) {
    const narrator = (item.narrator || '').toString().trim().toLowerCase();

    // Keep empty narrators at the end regardless of sort direction
    if (!narrator) {
      return direction === sortDirections.ASCENDING ? '\uffff' : '';
    }

    return narrator;
  },

  releaseDate: function(item, direction) {
    const releaseDate = item.releaseDate ? Date.parse(item.releaseDate) : Number.NaN;

    // Keep unknown release dates at the end regardless of sort direction
    if (Number.isNaN(releaseDate)) {
      return direction === sortDirections.ASCENDING ? Number.MAX_SAFE_INTEGER : Number.MIN_SAFE_INTEGER;
    }

    return releaseDate;
  },

  pageCount: function(item, direction) {
    const pageCount = item.pageCount;

    // Treat 0/undefined as unknown and keep it at the end regardless of direction
    if (pageCount == null || pageCount <= 0) {
      return direction === sortDirections.ASCENDING ? Number.MAX_SAFE_INTEGER : Number.MIN_SAFE_INTEGER;
    }

    return pageCount;
  },

  duration: function(item, direction) {
    const durationMinutes = item.durationMinutes;

    // Keep unknown durations at the end regardless of sort direction
    if (durationMinutes == null || durationMinutes <= 0) {
      return direction === sortDirections.ASCENDING ? Number.MAX_SAFE_INTEGER : Number.MIN_SAFE_INTEGER;
    }

    return durationMinutes;
  },

  rating: function(item) {
    return item.ratings.value;
  },

  status: function(item, _direction, selectedMediaType) {
    // Sort by the actual status displayed in BookStatus component
    // Group statuses properly so they sort together:
    // 1. Books with files (grouped by format from EditionInfo if available)
    // 2. Missing books (grouped together)
    // 3. Not Monitored books (grouped together)
    // 4. Future releases (grouped together)
    
    // Check multiple indicators for whether book has files
    // This handles both regular books and multi-edition expanded books
    const hasStatisticsFiles = item.statistics?.bookFileCount > 0;
    const hasEditionFiles = item.editionInfo?.bookFileCount > 0;
    const hasAnyFormats = Array.isArray(item.formats) && item.formats.length > 0;
    
    // Prefer media-type aware detection when possible
    let hasFilesForSelectedType = hasStatisticsFiles || hasEditionFiles;
    let selectedFormat = 'Unknown';
    
    if (hasAnyFormats) {
      const formats = item.formats.map((f) => (typeof f === 'string' ? f.toUpperCase() : String(f).toUpperCase()));
      const audioFormats = ['M4B', 'MP3', 'FLAC', 'AAC', 'OGG', 'WAV'];
      const ebookFormats = ['EPUB', 'PDF', 'MOBI', 'AZW', 'AZW3', 'KEPUB'];
      if (selectedMediaType === 'audiobook') {
        const audioHit = formats.find((f) => audioFormats.includes(f));
        if (audioHit) { hasFilesForSelectedType = true; selectedFormat = audioHit; }
      }
      else if (selectedMediaType === 'ebook') {
        const ebookHit = formats.find((f) => ebookFormats.includes(f));
        if (ebookHit) { hasFilesForSelectedType = true; selectedFormat = ebookHit; }
      }
    }
    
    const isAvailable = !item.releaseDate || Date.parse(item.releaseDate) < new Date();
    
    if (hasFilesForSelectedType) {
      // Has files - group first, then by preferred format order per media type
      let format = selectedFormat;
      if (format === 'Unknown' || !format) {
        // Try editionInfo formats
        if (item.editionInfo?.formats && item.editionInfo.formats.length > 0) {
          format = item.editionInfo.formats[0];
        }
        // Default based on selected media type
        if (!format) {
          if (selectedMediaType === 'audiobook') format = 'M4B';
          else if (selectedMediaType === 'ebook') format = 'EPUB';
          else format = 'Unknown';
        }
      }

      const fmt = (typeof format === 'string' ? format.toUpperCase() : String(format).toUpperCase());
      // Lower number = higher priority within the "has files" group
      const audioOrder = { M4B: 0, MP3: 1, FLAC: 2, AAC: 3, OGG: 4, WAV: 5 };
      const ebookOrder = { EPUB: 0, KEPUB: 1, AZW3: 2, MOBI: 3, PDF: 4 };
      const priority = selectedMediaType === 'ebook' ? (ebookOrder[fmt] ?? 9) : (audioOrder[fmt] ?? 9);

      return `1_${String(priority).padStart(2, '0')}_${fmt}`;
    }
    
    // Respect selected media type for monitored flag
    let isMonitored;
    if (selectedMediaType === 'audiobook') isMonitored = !!item.audiobookMonitored;
    else if (selectedMediaType === 'ebook') isMonitored = !!item.ebookMonitored;
    else isMonitored = !!(item.audiobookMonitored || item.ebookMonitored);
    
    if (!isMonitored) {
      return '3_Not Monitored';
    }
    
    if (isAvailable) {
      return '2_Missing';
    }
    
    return '4_Future Release';
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
  sortKey: 'releaseDate',
  sortDirection: sortDirections.DESCENDING,
  items: [],
  pendingChanges: {},
  sortPredicates: {
    rating: function(item) {
      return item.ratings.value;
    },
    durationMinutes: function(item, direction) {
      // Handle null values - they should always sort to the bottom
      if (item.durationMinutes == null) {
        // Return very high value for ascending (nulls last) or very low for descending (nulls last)
        return direction === sortDirections.ASCENDING ? Number.MAX_SAFE_INTEGER : Number.MIN_SAFE_INTEGER;
      }
      return item.durationMinutes;
    }
  },

  // Legacy columns for backward compatibility
  columns: [
    {
      name: 'select',
      columnLabel: 'Select',
      isSortable: false,
      isVisible: true,
      isModifiable: false,
      isHidden: true
    },
    {
      name: 'monitored',
      columnLabel: 'Monitored',
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'title',
      label: 'Title',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'narrator',
      label: 'Narrator',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'series',
      label: 'Series',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'releaseDate',
      label: 'Release Date',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'pageCount',
      label: 'Pages',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'duration',
      label: 'Duration',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'rating',
      label: 'Rating',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'indexerFlags',
      columnLabel: () => translate('IndexerFlags'),
      label: () => React.createElement(Icon, {
        name: icons.FLAG,
        title: () => translate('IndexerFlags')
      }),
      isVisible: false
    },
    {
      name: 'status',
      label: 'Status',
      isVisible: true,
      isSortable: true
    },
    {
      name: 'actions',
      columnLabel: 'Actions',
      isVisible: true,
      isModifiable: false
    }
  ],

  // Media type specific columns
  audiobookColumns: [
    {
      name: 'select',
      columnLabel: 'Select',
      isSortable: false,
      isVisible: true,
      isModifiable: false,
      isHidden: true
    },
    {
      name: 'monitored',
      columnLabel: 'Monitored',
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'title',
      label: 'Title',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'narrator',
      label: 'Narrator',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'series',
      label: 'Series',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'releaseDate',
      label: 'Release Date',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'duration',
      label: 'Duration',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'rating',
      label: 'Rating',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'indexerFlags',
      columnLabel: () => translate('IndexerFlags'),
      label: () => React.createElement(Icon, {
        name: icons.FLAG,
        title: () => translate('IndexerFlags')
      }),
      isVisible: false
    },
    {
      name: 'status',
      label: 'Status',
      isVisible: true,
      isSortable: true
    },
    {
      name: 'actions',
      columnLabel: 'Actions',
      isVisible: true,
      isModifiable: false
    }
  ],

  ebookColumns: [
    {
      name: 'select',
      columnLabel: 'Select',
      isSortable: false,
      isVisible: true,
      isModifiable: false,
      isHidden: true
    },
    {
      name: 'monitored',
      columnLabel: 'Monitored',
      isVisible: true,
      isModifiable: false
    },
    {
      name: 'title',
      label: 'Title',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'series',
      label: 'Series',
      isSortable: true,
      isVisible: false
    },
    {
      name: 'releaseDate',
      label: 'Release Date',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'pageCount',
      label: 'Pages',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'rating',
      label: 'Rating',
      isSortable: true,
      isVisible: true
    },
    {
      name: 'indexerFlags',
      columnLabel: () => translate('IndexerFlags'),
      label: () => React.createElement(Icon, {
        name: icons.FLAG,
        title: () => translate('IndexerFlags')
      }),
      isVisible: false
    },
    {
      name: 'status',
      label: 'Status',
      isVisible: true,
      isSortable: true
    },
    {
      name: 'actions',
      columnLabel: 'Actions',
      isVisible: true,
      isModifiable: false
    }
  ]
};

export const persistState = [
  'books.sortKey',
  'books.sortDirection',
  'books.columns',
  'books.audiobookColumns',
  'books.ebookColumns'
];

//
// Actions Types

export const FETCH_BOOKS = 'books/fetchBooks';
export const SET_BOOKS_SORT = 'books/setBooksSort';
export const SET_BOOKS_TABLE_OPTION = 'books/setBooksTableOption';
export const CLEAR_BOOKS = 'books/clearBooks';
export const SET_BOOK_VALUE = 'books/setBookValue';
export const SAVE_BOOK = 'books/saveBook';
export const DELETE_BOOK = 'books/deleteBook';
export const DELETE_AUTHOR_BOOKS = 'books/deleteAuthorBooks';
export const TOGGLE_BOOK_MONITORED = 'books/toggleBookMonitored';
export const TOGGLE_BOOKS_MONITORED = 'books/toggleBooksMonitored';

//
// Action Creators

export const fetchBooks = createThunk(FETCH_BOOKS);
export const setBooksSort = createAction(SET_BOOKS_SORT);
export const setBooksTableOption = createAction(SET_BOOKS_TABLE_OPTION);
export const clearBooks = createAction(CLEAR_BOOKS);
export const toggleBookMonitored = createThunk(TOGGLE_BOOK_MONITORED);
export const toggleBooksMonitored = createThunk(TOGGLE_BOOKS_MONITORED);

export const saveBook = createThunk(SAVE_BOOK);

export const deleteBook = createThunk(DELETE_BOOK, (payload) => {
  return {
    ...payload,
    queryParams: {
      deleteFiles: payload.deleteFiles,
      addImportListExclusion: payload.addImportListExclusion,
      applyToBothFormats: payload.applyToBothFormats
    }
  };
});

export const deleteAuthorBooks = createThunk(DELETE_AUTHOR_BOOKS, (payload) => {
  return {
    ...payload,
    queryParams: {
      authorId: payload.authorId
    }
  };
});

export const setBookValue = createAction(SET_BOOK_VALUE, (payload) => {
  return {
    section: 'books',
    ...payload
  };
});

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_BOOKS]: function(getState, payload, dispatch) {
    const requestId = ++currentFetchRequestId;

    if (abortCurrentFetchRequest) {
      abortCurrentFetchRequest();
      abortCurrentFetchRequest = null;
    }

    dispatch(set({ section, isFetching: true }));

    const { request, abortRequest } = createAjaxRequest({
      url: '/book',
      data: payload,
      traditional: true
    });

    abortCurrentFetchRequest = abortRequest;

    request.done((data) => {
      if (requestId !== currentFetchRequestId) {
        return;
      }

      abortCurrentFetchRequest = null;

      // Preserve books for other authors we didn't fetch
      if (payload.hasOwnProperty('authorId')) {
        const oldBooks = getState().books.items;
        const newBooks = oldBooks.filter((x) => x.authorId !== payload.authorId);
        data = newBooks.concat(data);
      }

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
      if (requestId !== currentFetchRequestId) {
        return;
      }

      abortCurrentFetchRequest = null;

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });

    return abortRequest;
  },

  [SAVE_BOOK]: createSaveProviderHandler(section, '/book'),
  [DELETE_BOOK]: createRemoveItemHandler(section, '/book'),

  [DELETE_AUTHOR_BOOKS]: function(getState, payload, dispatch) {
    const { authorId } = payload;
    const books = getState().books.items;

    const toDelete = books.filter((x) => x.authorId === authorId);

    dispatch(batchActions(toDelete.map((b) => removeItem({ section, id: b.id }))));
  },

  [TOGGLE_BOOK_MONITORED]: function(getState, payload, dispatch) {
    const {
      bookId,
      bookEntity = bookEntities.BOOKS,
      monitored
    } = payload;

    // Defensive: API expects a boolean, but UI can sometimes pass 0/1/2
    const monitoredBool = coerceMonitoredBoolean(monitored);

    const bookSection = _.last(bookEntity.split('.'));

    dispatch(updateItem({
      id: bookId,
      section: bookSection,
      isSaving: true
    }));

    const promise = createAjaxRequest({
      url: '/book/monitor',
      method: 'PUT',
      data: JSON.stringify({ bookIds: [bookId], monitored: monitoredBool }),
      dataType: 'json'
    }).request;

    promise.done((data) => {
      const books = Array.isArray(data) ? data : (data && data.books ? data.books : null);

      if (books && Array.isArray(books) && books.length) {
        const bookData = books[0];

        const updateFields = {
          id: bookId,
          section: bookSection,
          isSaving: false
        };

        if (bookData.hasOwnProperty('audiobookMonitored')) {
          updateFields.audiobookMonitored = bookData.audiobookMonitored;
        }
        if (bookData.hasOwnProperty('ebookMonitored')) {
          updateFields.ebookMonitored = bookData.ebookMonitored;
        }
        if (bookData.hasOwnProperty('monitored')) {
          updateFields.monitored = bookData.monitored;
        }

        dispatch(updateItem(updateFields));
        return;
      }

      dispatch(updateItem({
        id: bookId,
        section: bookSection,
        isSaving: false,
        monitored: monitoredBool
      }));
    });

    promise.fail((xhr) => {
      dispatch(updateItem({
        id: bookId,
        section: bookSection,
        isSaving: false
      }));
    });
  },

  [TOGGLE_BOOKS_MONITORED]: function(getState, payload, dispatch) {
    const {
      bookIds,
      bookEntity = bookEntities.BOOKS,
      monitored,
      mediaType
    } = payload;

    // Defensive: API expects a boolean, but UI can sometimes pass 0/1/2
    const monitoredBool = coerceMonitoredBoolean(monitored);

    dispatch(batchActions(
      bookIds.map((bookId) => {
        return updateItem({
          id: bookId,
          section: bookEntity,
          isSaving: true
        });
      })
    ));

    const promise = createAjaxRequest({
      url: '/book/monitor',
      method: 'PUT',
      data: JSON.stringify({ bookIds, monitored: monitoredBool }),
      dataType: 'json'
    }).request;

    promise.done((data) => {
      // Handle different response formats - data might be the array directly or wrapped in a response object
      const books = Array.isArray(data) ? data : (data && data.books ? data.books : null);
      
      if (books && Array.isArray(books)) {
        dispatch(batchActions(
          books.map((bookData) => {
            // Only update the monitoring status fields - don't touch author data!
            const updateFields = {
              id: bookData.id,
              section: bookEntity,
              isSaving: false
            };

            // Only copy over the monitoring-related fields from the response
            if (bookData.hasOwnProperty('audiobookMonitored')) {
              updateFields.audiobookMonitored = bookData.audiobookMonitored;
            }
            if (bookData.hasOwnProperty('ebookMonitored')) {
              updateFields.ebookMonitored = bookData.ebookMonitored;
            }
            if (bookData.hasOwnProperty('monitored')) {
              updateFields.monitored = bookData.monitored;
            }

            return updateItem(updateFields);
          })
        ));
      } else {
        // Fallback: update local state if server doesn't return books data
        dispatch(batchActions(
          bookIds.map((bookId) => {
            const updateFields = {
              id: bookId,
              section: bookEntity,
              isSaving: false
            };

            if (mediaType === 'audiobook') {
              updateFields.audiobookMonitored = monitoredBool;
            } else if (mediaType === 'ebook') {
              updateFields.ebookMonitored = monitoredBool;
            } else {
              // Fallback for legacy support
              updateFields.monitored = monitoredBool;
            }

            return updateItem(updateFields);
          })
        ));
      }
    });

    promise.fail((xhr) => {
      dispatch(batchActions(
        bookIds.map((bookId) => {
          return updateItem({
            id: bookId,
            section: bookEntity,
            isSaving: false
          });
        })
      ));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_BOOKS_SORT]: createSetClientSideCollectionSortReducer(section),

  [SET_BOOKS_TABLE_OPTION]: (state, { payload }) => {
    const { mediaType, ...tableOptions } = payload;
    
    // If no mediaType specified, update the legacy columns for backward compatibility
    if (!mediaType) {
      return createSetTableOptionReducer(section)(state, { payload: tableOptions });
    }
    
    // Update media-type specific columns
    const columnKey = mediaType === 'audiobook' ? 'audiobookColumns' : 'ebookColumns';
    
    // Handle column updates
    if (tableOptions.columns) {
      return Object.assign({}, state, {
        [columnKey]: tableOptions.columns
      });
    }
    
    // Handle other table options
    return Object.assign({}, state, tableOptions);
  },

  [SET_BOOK_VALUE]: createSetSettingValueReducer(section),

  [CLEAR_BOOKS]: (state) => {
    return Object.assign({}, state, {
      isFetching: false,
      isPopulated: false,
      error: null,
      items: [],
      itemMap: {}
    });
  }

}, defaultState, section);
