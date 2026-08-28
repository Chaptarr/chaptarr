import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import createRemoveItemHandler from 'Store/Actions/Creators/createRemoveItemHandler';
import { createCancelSaveProviderHandler } from 'Store/Actions/Creators/createSaveProviderHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getProviderState from 'Utilities/State/getProviderState';
import { set, update, updateItem } from '../baseActions';

function normalizeRootFolder(rootFolder) {
  if (!rootFolder) {
    return rootFolder;
  }

  const normalizedFolderType = coerceFolderType(rootFolder.folderType);
  if (normalizedFolderType === rootFolder.folderType) {
    return rootFolder;
  }

  return {
    ...rootFolder,
    folderType: normalizedFolderType
  };
}

function normalizeRootFolderData(data) {
  if (Array.isArray(data)) {
    return data.map(normalizeRootFolder);
  }

  return normalizeRootFolder(data);
}

//
// Variables

export const section = 'settings.rootFolders';

//
// Actions Types

export const FETCH_ROOT_FOLDERS = 'settings/rootFolders/fetchRootFolders';
export const SET_ROOT_FOLDER_VALUE = 'settings/rootFolders/setRootFolderValue';
export const SAVE_ROOT_FOLDER = 'settings/rootFolders/saveRootFolder';
export const CANCEL_SAVE_ROOT_FOLDER = 'settings/rootFolders/cancelSaveRootFolder';
export const DELETE_ROOT_FOLDER = 'settings/rootFolders/deleteRootFolder';

//
// Action Creators

export const fetchRootFolders = createThunk(FETCH_ROOT_FOLDERS);
export const saveRootFolder = createThunk(SAVE_ROOT_FOLDER);
export const cancelSaveRootFolder = createThunk(CANCEL_SAVE_ROOT_FOLDER);
export const deleteRootFolder = createThunk(DELETE_ROOT_FOLDER);

export const setRootFolderValue = createAction(SET_ROOT_FOLDER_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Details

export default {
  //
  // State

  defaultState: {
    isFetching: false,
    isPopulated: false,
    error: null,
    isDeleting: false,
    deleteError: null,
    schema: {
      name: '',
      path: '',
      folderType: 1, // Default to audiobook
      placeEbooksWithAudiobooks: false,
      isCalibreLibrary: false,
      host: 'localhost',
      port: 8080,
      urlBase: '',
      username: '',
      password: '',
      library: '',
      outputFormat: '',
      outputProfile: 'default',
      useSsl: false,
      // REMOVED: defaultQualityProfileId, defaultMetadataProfileId, defaultAudiobookMetadataProfileId, defaultEbookMetadataProfileId - no longer used
      // Per-media-type monitoring defaults. The gate is independent from the
      // one-time current-book seed and the ongoing catalog-relative policy.
      audiobookMonitored: true,
      audiobookMonitorExistingMode: 'all',
      audiobookMonitorNewItems: 'all',
      ebookMonitored: true,
      ebookMonitorExistingMode: 'all',
      ebookMonitorNewItems: 'all',
      // Per-media-type profile settings (null means unconfigured; UI auto-selects defaults)
      audiobookQualityProfileId: null,
      audiobookMetadataProfileId: null,
      ebookQualityProfileId: null,
      ebookMetadataProfileId: null,
      // Per-media-type AudioBookShelf sidecar settings
      audiobookWriteAudioBookShelfMetadataJson: false,
      audiobookWriteAudioBookShelfCover: false,
      ebookWriteAudioBookShelfMetadataJson: false,
      ebookWriteAudioBookShelfCover: false,
      // Per-media-type tag settings
      audiobookTags: [],
      ebookTags: [],
      defaultTags: []
    },
    isSaving: false,
    saveError: null,
    items: [],
    pendingChanges: {}
  },

  //
  // Action Handlers

  actionHandlers: {

    [FETCH_ROOT_FOLDERS]: function(getState, payload, dispatch) {
      dispatch(set({ section, isFetching: true }));

      const payloadOrEmpty = payload || {};
      const { id, ...otherPayload } = payloadOrEmpty;

      const { request, abortRequest } = createAjaxRequest({
        url: id == null ? '/rootFolder' : `/rootFolder/${id}`,
        data: otherPayload,
        traditional: true
      });

      request.done((data) => {
        const normalized = normalizeRootFolderData(data);

        dispatch(batchActions([
          id == null ? update({ section, data: normalized }) : updateItem({ section, ...normalized }),

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

    [SAVE_ROOT_FOLDER]: function(getState, payload, dispatch) {
      // Custom save handler for root folders that properly constructs payload based on mixed content setting
      dispatch(set({ section, isSaving: true }));

      const normalizeOptionalId = (value) => {
        return typeof value === 'number' && value > 0 ? value : null;
      };

      const normalizeTags = (value) => {
        return Array.isArray(value) ? value : [];
      };

      const { id, ...otherPayload } = payload;
      const rawData = getProviderState({ id, ...otherPayload }, getState, section);

      // Build a clean payload with explicit field handling
      const saveData = {
        name: rawData.name,
        path: rawData.path,
        folderType: coerceFolderType(rawData.folderType),
        defaultTags: rawData.defaultTags,
        isCalibreLibrary: rawData.isCalibreLibrary,
        defaultSyncMonitoredAcrossFormats: rawData.defaultSyncMonitoredAcrossFormats
      };

      // Add Calibre settings if it's a Calibre library
      if (rawData.isCalibreLibrary) {
        saveData.host = rawData.host;
        saveData.port = rawData.port;
        saveData.urlBase = rawData.urlBase;
        saveData.username = rawData.username;
        saveData.password = rawData.password;
        saveData.library = rawData.library;
        saveData.outputFormat = rawData.outputFormat;
        saveData.outputProfile = rawData.outputProfile;
        saveData.useSsl = rawData.useSsl;
      }

      // Handle media-specific settings based on folderType (authoritative source)
      // folderType: 0 = Mixed, 1 = Audiobook, 2 = Ebook

      const folderType = saveData.folderType;
      saveData.placeEbooksWithAudiobooks = folderType === FolderType.Mixed ? !!rawData.placeEbooksWithAudiobooks : false;

      if (folderType === FolderType.Audiobook) {
        // Audiobook-only folder
        saveData.audiobookMonitored = rawData.audiobookMonitored;
        saveData.audiobookMonitorExistingMode = rawData.audiobookMonitorExistingMode;
        saveData.audiobookMonitorNewItems = rawData.audiobookMonitorNewItems;
        saveData.audiobookQualityProfileId = normalizeOptionalId(rawData.audiobookQualityProfileId);
        saveData.audiobookMetadataProfileId = normalizeOptionalId(rawData.audiobookMetadataProfileId);
        saveData.audiobookWriteAudioBookShelfMetadataJson = !!rawData.audiobookWriteAudioBookShelfMetadataJson;
        saveData.audiobookWriteAudioBookShelfCover = !!rawData.audiobookWriteAudioBookShelfCover;
        saveData.audiobookTags = normalizeTags(rawData.audiobookTags);

        // Explicitly null ALL ebook fields
        saveData.ebookMonitored = null;
        saveData.ebookMonitorExistingMode = null;
        saveData.ebookMonitorNewItems = null;
        saveData.ebookQualityProfileId = null;
        saveData.ebookMetadataProfileId = null;
        saveData.ebookWriteAudioBookShelfMetadataJson = null;
        saveData.ebookWriteAudioBookShelfCover = null;
        saveData.ebookTags = null;

      } else if (folderType === FolderType.Ebook) {
        // Ebook-only folder
        saveData.ebookMonitored = rawData.ebookMonitored;
        saveData.ebookMonitorExistingMode = rawData.ebookMonitorExistingMode;
        saveData.ebookMonitorNewItems = rawData.ebookMonitorNewItems;
        saveData.ebookQualityProfileId = normalizeOptionalId(rawData.ebookQualityProfileId);
        saveData.ebookMetadataProfileId = normalizeOptionalId(rawData.ebookMetadataProfileId);
        saveData.ebookWriteAudioBookShelfMetadataJson = !!rawData.ebookWriteAudioBookShelfMetadataJson;
        saveData.ebookWriteAudioBookShelfCover = !!rawData.ebookWriteAudioBookShelfCover;
        saveData.ebookTags = normalizeTags(rawData.ebookTags);

        // Explicitly null ALL audiobook fields
        saveData.audiobookMonitored = null;
        saveData.audiobookMonitorExistingMode = null;
        saveData.audiobookMonitorNewItems = null;
        saveData.audiobookQualityProfileId = null;
        saveData.audiobookMetadataProfileId = null;
        saveData.audiobookWriteAudioBookShelfMetadataJson = null;
        saveData.audiobookWriteAudioBookShelfCover = null;
        saveData.audiobookTags = null;

      } else if (folderType === FolderType.Mixed) {
        // Mixed content folder (folderType=0)
        saveData.audiobookMonitored = rawData.audiobookMonitored;
        saveData.audiobookMonitorExistingMode = rawData.audiobookMonitorExistingMode;
        saveData.audiobookMonitorNewItems = rawData.audiobookMonitorNewItems;
        saveData.audiobookQualityProfileId = normalizeOptionalId(rawData.audiobookQualityProfileId);
        saveData.audiobookMetadataProfileId = normalizeOptionalId(rawData.audiobookMetadataProfileId);
        saveData.audiobookWriteAudioBookShelfMetadataJson = !!rawData.audiobookWriteAudioBookShelfMetadataJson;
        saveData.audiobookWriteAudioBookShelfCover = !!rawData.audiobookWriteAudioBookShelfCover;
        saveData.audiobookTags = normalizeTags(rawData.audiobookTags);

        saveData.ebookMonitored = rawData.ebookMonitored;
        saveData.ebookMonitorExistingMode = rawData.ebookMonitorExistingMode;
        saveData.ebookMonitorNewItems = rawData.ebookMonitorNewItems;
        saveData.ebookQualityProfileId = normalizeOptionalId(rawData.ebookQualityProfileId);
        saveData.ebookMetadataProfileId = normalizeOptionalId(rawData.ebookMetadataProfileId);
        saveData.ebookWriteAudioBookShelfMetadataJson = !!rawData.ebookWriteAudioBookShelfMetadataJson;
        saveData.ebookWriteAudioBookShelfCover = !!rawData.ebookWriteAudioBookShelfCover;
        saveData.ebookTags = normalizeTags(rawData.ebookTags);

      } else {
        // Invalid folderType - log error and abort
        console.error('[ROOT FOLDER SAVE] Invalid folderType:', rawData.folderType);
        dispatch(set({
          section,
          isSaving: false,
          saveError: { message: 'Invalid folder type. Please refresh and try again.' }
        }));
        return;
      }

      // Never send deprecated generic or legacy fields
      // saveData.defaultQualityProfileId - NEVER
      // saveData.defaultMetadataProfileId - NEVER

      const requestUrl = id ? `/rootFolder/${id}` : '/rootFolder';

      const { request } = createAjaxRequest({
        url: requestUrl,
        method: id ? 'PUT' : 'POST',
        contentType: 'application/json',
        data: JSON.stringify(saveData),
        dataType: 'json'
      });

      request.done((data) => {
        const normalized = normalizeRootFolder(data);

        dispatch(batchActions([
          updateItem({ section, ...normalized }),
          set({
            section,
            isSaving: false,
            saveError: null,
            pendingChanges: {}
          })
        ]));
      });

      request.fail((xhr) => {
        dispatch(set({
          section,
          isSaving: false,
          saveError: xhr
        }));
      });
    },
    [CANCEL_SAVE_ROOT_FOLDER]: createCancelSaveProviderHandler(section),
    [DELETE_ROOT_FOLDER]: createRemoveItemHandler(section, '/rootFolder')

  },

  //
  // Reducers

  reducers: {
    [SET_ROOT_FOLDER_VALUE]: createSetSettingValueReducer(section)
  }
};
