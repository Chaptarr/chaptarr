import { createAction } from 'redux-actions';
import { createThunk } from 'Store/thunks';
import createHandleActions from './Creators/createHandleActions';

//
// Variables
//

export const section = 'quickstart';
const QUICKSTART_PROGRESS_VERSION = 1;

//
// State
//

export const defaultState = {
  interactions: {
    optionalConnections: false,
    audioBookShelf: false,
    indexers: false,
    downloadClients: false,
    customFormats: false,
    qualityProfiles: false,
    enhancedSearching: false,
    metadataProfiles: false,
    matching: false,
    rootFolders: false
  },
  // Track install ID to detect fresh installs
  installId: null
};

//
// Actions Types
//

export const MARK_SECTION_INTERACTED = 'quickstart/markSectionInteracted';
export const LOAD_QUICKSTART_STATE = 'quickstart/loadQuickstartState';

//
// Action Creators
//

export const markSectionInteracted = createAction(MARK_SECTION_INTERACTED);
export const loadQuickstartState = createAction(LOAD_QUICKSTART_STATE);

//
// Thunks
//

export const markSectionAsInteracted = createThunk(MARK_SECTION_INTERACTED);

//
// Reducers
//

export const reducers = createHandleActions({
  [MARK_SECTION_INTERACTED]: (state, { payload }) => {
    const newState = {
      ...state,
      interactions: {
        ...state.interactions,
        [payload.section]: true
      }
    };

    // Save to localStorage
    localStorage.setItem('quickstartProgress', JSON.stringify(newState.interactions));
    localStorage.setItem('quickstartProgressVersion', `${QUICKSTART_PROGRESS_VERSION}`);

    return newState;
  },

  [LOAD_QUICKSTART_STATE]: (state, { payload } = {}) => {
    const savedProgress = localStorage.getItem('quickstartProgress');
    const savedInstallId = localStorage.getItem('quickstartInstallId');
    const savedProgressVersion = localStorage.getItem('quickstartProgressVersion');

    const currentInstallId = payload?.installationId || null;

    // If the progress schema changes, clear any stale progress.
    if (savedProgressVersion !== `${QUICKSTART_PROGRESS_VERSION}`) {
      localStorage.removeItem('quickstartProgress');
      localStorage.setItem('quickstartProgressVersion', `${QUICKSTART_PROGRESS_VERSION}`);

      if (currentInstallId) {
        localStorage.setItem('quickstartInstallId', currentInstallId);
      }

      return {
        ...state,
        interactions: { ...defaultState.interactions },
        installId: currentInstallId || savedInstallId
      };
    }

    // If the saved install ID doesn't match the current server installation, clear stale progress.
    if (currentInstallId && savedInstallId !== currentInstallId) {
      localStorage.removeItem('quickstartProgress');
      localStorage.setItem('quickstartInstallId', currentInstallId);

      return {
        ...state,
        interactions: { ...defaultState.interactions },
        installId: currentInstallId
      };
    }

    const canUseSavedProgress = !currentInstallId || savedInstallId === currentInstallId;

    if (canUseSavedProgress && savedProgress) {
      try {
        const savedInteractions = JSON.parse(savedProgress);
        return {
          ...state,
          interactions: {
            ...defaultState.interactions,
            ...savedInteractions
          },
          installId: currentInstallId || savedInstallId
        };
      } catch (e) {
        console.error('Failed to load quickstart progress from localStorage', e);
        localStorage.removeItem('quickstartProgress');
      }
    }

    if (currentInstallId && !savedInstallId) {
      localStorage.setItem('quickstartInstallId', currentInstallId);
    }

    return {
      ...state,
      interactions: { ...defaultState.interactions },
      installId: currentInstallId || savedInstallId
    };
  }
}, defaultState, section);
