import { createAction } from 'redux-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { set } from './baseActions';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'narrator';

//
// State

export const defaultState = {
  discovery: {},
  isSearching: false,
  isSettingPreferred: false,
  error: null
};

//
// Actions Types

export const FETCH_NARRATOR_DISCOVERY = 'narrator/fetchNarratorDiscovery';
export const CLEAR_NARRATOR_DISCOVERY = 'narrator/clearNarratorDiscovery';
export const ADD_NARRATOR_VARIANT = 'narrator/addNarratorVariant';

//
// Action Creators

export const fetchNarratorDiscovery = createThunk(FETCH_NARRATOR_DISCOVERY);
export const clearNarratorDiscovery = createAction(CLEAR_NARRATOR_DISCOVERY);
export const addNarratorVariant = createThunk(ADD_NARRATOR_VARIANT);

// Optimistic state update helper: move narrator from available → existing
function optimisticallyMoveNarrator(state, bookId, narrator) {
  const name = (typeof narrator === 'string') ? narrator : narrator.name;
  const editionId = (narrator && typeof narrator === 'object') ? narrator.editionId : null;
  const discovery = { ...(state.narrator.discovery || {}) };
  const prev = discovery[bookId] || {};
  const filtered = prev.filteredNarrators || [];
  const nameLower = (name || '').toLowerCase();
  const selected = editionId ?
    filtered.find((n) => n.editionId === editionId) :
    filtered.find((n) => ((n.name || '').toLowerCase() === nameLower));
  const existingCopies = prev.existingCopyNarrators || [];
  const selectedHasFiles = (selected?.bookFileCount || 0) > 0;
  const selectedForStatus = selected || { name, editionId };

  return {
    ...discovery,
    [bookId]: {
      ...prev,
      filteredNarrators: editionId ?
        filtered.filter((n) => n.editionId !== editionId) :
        filtered.filter((n) => ((n.name || '').toLowerCase() !== nameLower)),
      existingCopyNarrators: selectedHasFiles ?
        [
          ...existingCopies,
          {
            ...selectedForStatus,
            status: 'existing'
          }
        ] :
        [
          ...existingCopies,
          {
            ...selectedForStatus,
            monitored: true,
            status: 'monitored'
          }
        ],
      totalExisting: selectedHasFiles ? (prev.totalExisting || 0) + 1 : (prev.totalExisting || 0),
      totalFiltered: Math.max((prev.totalFiltered || 0) - 1, 0),
      totalAvailable: Math.max((prev.totalAvailable || 0) - 1, 0)
    }
  };
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [FETCH_NARRATOR_DISCOVERY]: function(getState, payload, dispatch) {
    const { bookId } = payload;

    dispatch(set({
      section,
      isSearching: true,
      error: null
    }));

    // Use editions endpoint to get narrator variants
    const endpoint = `/edition?bookId=${bookId}`;

    const promise = createAjaxRequest({
      url: endpoint,
      method: 'GET'
    }).request;

    promise.done((editions) => {
      const normalizeNarratorNames = (names) => {
        if (!Array.isArray(names)) {
          return [];
        }

        return names
          .map((name) => (typeof name === 'string' ? name.trim() : ''))
          .filter((name) => name);
      };

      const getEditionNarratorNames = (edition) => {
        const names = normalizeNarratorNames(edition?.narratorNames);
        if (!names.length) {
          const narrator = (edition?.narrator || '').trim();
          return narrator ? [narrator] : [];
        }

        // Prefer real narrator names over the "Full Cast" label if both are present.
        const withoutFullCast = names.filter((name) => name.toLowerCase() !== 'full cast');
        return withoutFullCast.length ? withoutFullCast : names;
      };

      const currentState = getState();
      const book = currentState.books.items.find((b) => b.id === bookId) || {};

      const narratorEditions = (editions || [])
        .map((edition) => {
          const narratorNamesForDisplay = getEditionNarratorNames(edition);

          return {
            ...edition,
            narratorNamesForDisplay,
            narratorNameForDisplay: narratorNamesForDisplay.join(', ')
          };
        })
        .filter((edition) => edition.narratorNamesForDisplay.length > 0);

      const existingEditions = narratorEditions.filter((e) =>
        e.bookFileCount > 0
      );

      const monitoredEditions = narratorEditions.filter((e) =>
        e.bookFileCount <= 0 && e.monitored
      );

      const availableEditions = narratorEditions.filter((e) =>
        e.bookFileCount <= 0 && !e.monitored && !e.monitoredByAnotherAudiobookBook
      );

      const mapEdition = (edition, status) => {
        const editionPhoto = edition.images && edition.images.length > 0 ?
          edition.images.find((img) => img.coverType === 'cover')?.url || edition.images[0].url :
          null;

        return {
          title: edition.title,
          subtitle: edition.subtitle,
          disambiguation: edition.disambiguation,
          name: edition.narratorNameForDisplay,
          narratorNames: edition.narratorNamesForDisplay,
          // Edition choices must show that edition's own artwork. Borrowing the
          // monitored book cover here makes an unillustrated narrator edition look
          // like it has another edition's cover.
          photo: editionPhoto,
          rating: edition.ratings?.value,
          voteCount: edition.ratings?.votes,
          publisher: edition.publisher || '',
          releaseDate: edition.releaseDate || null,
          overview: edition.overview || '',
          duration: edition.durationSeconds ? Math.floor(edition.durationSeconds / 60) : null,
          editionId: edition.id,
          format: edition.format,
          language: edition.language,
          monitored: edition.monitored,
          monitoredByAnotherAudiobookBook: edition.monitoredByAnotherAudiobookBook,
          bookFileCount: edition.bookFileCount,
          status
        };
      };

      const transformedData = {
        success: true,
        bookId,
        bookTitle: book.title || '',
        authorName: book.authorName || '',
        currentNarrator: book.narrator || '',
        totalPhysicalCopies: existingEditions.length,
        totalAvailable: availableEditions.length,
        totalFiltered: availableEditions.length,
        totalExisting: existingEditions.length + monitoredEditions.length,
        filteredNarrators: availableEditions.map((edition) => mapEdition(edition, 'available')),
        existingCopyNarrators: [
          ...existingEditions.map((edition) => mapEdition(edition, 'existing')),
          ...monitoredEditions.map((edition) => mapEdition(edition, 'monitored'))
        ],
        errorMessage: null,
        recommendedAction: null
      };

      // Get current discovery state to properly merge
      const currentDiscovery = currentState.narrator.discovery || {};

      dispatch(set({
        section,
        discovery: {
          ...currentDiscovery,
          [bookId]: transformedData
        },
        isSearching: false
      }));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isSearching: false,
        error: xhr
      }));
    });
  },

  [ADD_NARRATOR_VARIANT]: function(getState, payload, dispatch) {
    const { bookId, narrator } = payload;

    dispatch(set({
      section,
      isSettingPreferred: true,
      error: null
    }));

    // Get the current book data to duplicate
    const state = getState();
    const currentBook = state.books.items.find((b) => b.id === bookId);

    if (!currentBook) {
      dispatch(set({
        section,
        isSettingPreferred: false,
        error: { message: translate('BookNotFound') }
      }));
      return;
    }

    const selectedEditionId = (narrator && typeof narrator === 'object') ?
      (narrator.editionId || narrator.EditionId) :
      null;

    if (selectedEditionId) {
      // Track this narration alongside whatever the book already wants rather than
      // replacing it. The server decides whether a separate row is needed and returns
      // the existing one when this narrator is already tracked.
      const promise = createAjaxRequest({
        url: `/book/${bookId}/editions/wanted`,
        method: 'POST',
        data: JSON.stringify({
          editionId: selectedEditionId,
          searchForNewBook: payload.searchForNewBook === true,
          asNewVariant: true
        }),
        contentType: 'application/json'
      }).request;

      promise.done((data) => {
        dispatch(set({
          section,
          isSettingPreferred: false,
          discovery: optimisticallyMoveNarrator(getState(), bookId, narrator)
        }));
        if (payload.onSuccess) {
          payload.onSuccess(data);
        }
      });

      promise.fail((xhr) => {
        dispatch(set({
          section,
          isSettingPreferred: false,
          error: xhr
        }));
      });
    } else {
      // Fallback: Create a new book copy with this narrator (no edition selected)
      const newBookData = {
        ...currentBook,
        id: 0, // Reset ID for new book
        authorId: currentBook.authorId, // Preserve authorId for narrator variant
        narrator: narrator.name || narrator,
        monitored: true, // User clicked add - they want this book monitored
        statistics: {
          bookFileCount: 0,
          bookCount: 0,
          totalBookCount: 0,
          sizeOnDisk: 0,
          percentOfBooks: 0
        },
        added: new Date().toISOString(),
        addOptions: {
          monitor: 'all',
          searchForNewBook: payload.searchForNewBook === true,
          addType: 'manual' // Mark as manual addition since user selected it
        },
        // Include a single monitored edition for this narrator variant
        editions: [{
          title: currentBook.title,
          monitored: true, // User clicked add - they want THIS narrator monitored
          manualAdd: true, // User manually selected this narrator variant
          narrator: narrator.name || narrator,
          id: 0,
          bookId: 0 // Will be set by backend
        }]
      };

      // Remove fields that shouldn't be in POST request
      delete newBookData.author;
      delete newBookData.series;
      delete newBookData.bookFiles;

      const promise = createAjaxRequest({
        url: '/book',
        method: 'POST',
        data: JSON.stringify(newBookData),
        contentType: 'application/json'
      }).request;

      promise.done((data) => {
        dispatch(set({
          section,
          isSettingPreferred: false,
          discovery: optimisticallyMoveNarrator(getState(), bookId, narrator)
        }));
        if (payload.onSuccess) {
          payload.onSuccess(data);
        }
      });

      promise.fail((xhr) => {
        dispatch(set({
          section,
          isSettingPreferred: false,
          error: xhr
        }));
      });
    }
  }
});

//
// Reducers

export const reducers = createHandleActions({
  [CLEAR_NARRATOR_DISCOVERY]: function(state, { payload }) {
    const { bookId } = payload;
    const discovery = { ...state.discovery };
    delete discovery[bookId];

    return {
      ...state,
      discovery
    };
  }
}, defaultState, section);
