import { createAction } from 'redux-actions';
import { EditionReadingFormat } from 'Helpers/Props/editionReadingFormats';
import { createThunk, handleThunks } from 'Store/thunks';
import getNarratorDisplay from 'Utilities/Book/getNarratorDisplay';
import getNarratorNamesForDisplay from 'Utilities/Book/getNarratorNamesForDisplay';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import { showMessage } from './appActions';
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
  additions: {},
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

function moveEditionToLibrary(state, bookId, narrator, book) {
  const editionId = narrator.editionId;
  const discovery = { ...(state.narrator.discovery || {}) };
  const prev = discovery[bookId];

  if (!prev) {
    return discovery;
  }

  const available = prev.filteredNarrators || [];
  const existingCopies = prev.existingCopyNarrators || [];
  const selected = available.find((edition) => edition.editionId === editionId) ||
    existingCopies.find((edition) => edition.editionId === editionId) || narrator;
  const filteredNarrators = available.filter((edition) => edition.editionId !== editionId);
  const existingCopyNarrators = [
    ...existingCopies.filter((edition) => edition.editionId !== editionId),
    {
      ...selected,
      existingCopyId: book.id,
      existingCopyMonitored: book.monitored,
      status: 'existing'
    }
  ];

  return {
    ...discovery,
    [bookId]: {
      ...prev,
      filteredNarrators,
      existingCopyNarrators,
      totalExisting: existingCopyNarrators.length,
      totalFiltered: filteredNarrators.length,
      totalAvailable: filteredNarrators.length
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
      const currentState = getState();
      const book = currentState.books.items.find((b) => b.id === bookId) || {};

      const narratorEditions = (editions || [])
        .filter((edition) => edition.readingFormatId === EditionReadingFormat.Audiobook)
        .map((edition) => {
          const narratorNamesForDisplay = getNarratorNamesForDisplay(edition.narrators, edition.narratorNames?.length ? edition.narratorNames : [edition.narrator]);

          return {
            ...edition,
            narratorNamesForDisplay,
            narratorNameForDisplay: narratorNamesForDisplay.join(', ')
          };
        })
        .filter((edition) => edition.narratorNamesForDisplay.length > 0);

      const existingEditions = narratorEditions.filter((e) =>
        e.existingCopyId > 0
      );

      const availableEditions = narratorEditions.filter((e) =>
        !e.existingCopyId
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
          existingCopyId: edition.existingCopyId,
          existingCopyMonitored: edition.existingCopyMonitored,
          status
        };
      };

      const transformedData = {
        success: true,
        bookId,
        bookTitle: book.title || '',
        authorName: book.authorName || '',
        currentNarrator: book.narrator || '',
        totalAvailable: availableEditions.length,
        totalFiltered: availableEditions.length,
        totalExisting: existingEditions.length,
        filteredNarrators: availableEditions.map((edition) => mapEdition(edition, 'available')),
        existingCopyNarrators: existingEditions.map((edition) => mapEdition(edition, 'existing')),
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

    const selectedEditionId = narrator?.editionId ?? null;
    const key = `${bookId}:${selectedEditionId}`;
    const setAddition = (isSaving, error = null) => dispatch(set({
      section,
      additions: { ...getState().narrator.additions, [key]: { isSaving, error } }
    }));
    setAddition(true);

    const currentBook = getState().books.items.find((b) => b.id === bookId);
    if (!currentBook) {
      setAddition(false, { message: translate('BookNotFound') });
      return;
    }

    if (!Number.isInteger(selectedEditionId) || selectedEditionId <= 0) {
      setAddition(false, { message: translate('SelectEdition') });
      return;
    }

    // Add a separate wanted copy, or return the row already targeting this edition.
    const promise = createAjaxRequest({
      url: `/book/${bookId}/editions/wanted`,
      method: 'POST',
      data: JSON.stringify({ editionId: selectedEditionId, searchForNewBook: payload.searchForNewBook === true }),
      contentType: 'application/json'
    }).request;

    promise.done((data) => {
      if (data.existingCopy) {
        dispatch(showMessage({
          id: `wanted-edition-existing-${data.id}`,
          name: 'WantedEditionExistingCopy',
          message: translate(data.monitored === false ? 'WantedEditionExistingCopyUnmonitored' : 'WantedEditionExistingCopy', {
            title: data.title,
            narrators: getNarratorDisplay(narrator) || narrator.name || translate('Unknown')
          }),
          type: 'info',
          hideAfter: 10
        }));
      }

      dispatch(set({
        section,
        discovery: moveEditionToLibrary(getState(), bookId, narrator, data)
      }));
      setAddition(false);
      if (payload.onSuccess) {
        payload.onSuccess(data);
      }
    });

    promise.fail((xhr) => {
      setAddition(false, xhr);
    });
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
      additions: Object.fromEntries(Object.entries(state.additions).filter(([key]) => !key.startsWith(`${bookId}:`))),
      discovery
    };
  }
}, defaultState, section);
