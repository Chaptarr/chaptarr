import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { set, update } from 'Store/Actions/baseActions';
import createFetchSchemaHandler from 'Store/Actions/Creators/createFetchSchemaHandler';
import createRemoveItemHandler from 'Store/Actions/Creators/createRemoveItemHandler';
import createSaveProviderHandler from 'Store/Actions/Creators/createSaveProviderHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import updateSectionState from 'Utilities/State/updateSectionState';

//
// Variables

const section = 'settings.qualityProfiles';

function createSchemaUrl(payload) {
  const { profileType, mediaType } = payload || {};
  const requestedMediaType = profileType || mediaType;

  if (!requestedMediaType) {
    return '/qualityprofile/schema';
  }

  return `/qualityprofile/schema?mediaType=${encodeURIComponent(requestedMediaType)}`;
}

//
// Actions Types

export const FETCH_QUALITY_PROFILES = 'settings/qualityProfiles/fetchQualityProfiles';
export const FETCH_QUALITY_PROFILE_SCHEMA = 'settings/qualityProfiles/fetchQualityProfileSchema';
export const SAVE_QUALITY_PROFILE = 'settings/qualityProfiles/saveQualityProfile';
export const DELETE_QUALITY_PROFILE = 'settings/qualityProfiles/deleteQualityProfile';
export const SET_QUALITY_PROFILE_VALUE = 'settings/qualityProfiles/setQualityProfileValue';
export const CLONE_QUALITY_PROFILE = 'settings/qualityProfiles/cloneQualityProfile';

//
// Action Creators

export const fetchQualityProfiles = createThunk(FETCH_QUALITY_PROFILES);
export const fetchQualityProfileSchema = createThunk(FETCH_QUALITY_PROFILE_SCHEMA);
export const saveQualityProfile = createThunk(SAVE_QUALITY_PROFILE);
export const deleteQualityProfile = createThunk(DELETE_QUALITY_PROFILE);

export const setQualityProfileValue = createAction(SET_QUALITY_PROFILE_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

export const cloneQualityProfile = createAction(CLONE_QUALITY_PROFILE);

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
    isSchemaFetching: false,
    isSchemaPopulated: false,
    schemaError: null,
    schema: {},
    isSaving: false,
    saveError: null,
    items: [],
    pendingChanges: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_QUALITY_PROFILES]: function(getState, payload, dispatch) {
      dispatch(set({ section, isFetching: true }));

      const { mediaType, ...otherPayload } = payload || {};
      let url = '/qualityprofile';

      if (mediaType) {
        url += `?mediaType=${encodeURIComponent(mediaType)}`;
      }

      const { request, abortRequest } = createAjaxRequest({
        url,
        data: otherPayload,
        traditional: true
      });

      request.done((data) => {
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
        dispatch(set({
          section,
          isFetching: false,
          isPopulated: false,
          error: xhr.aborted ? null : xhr
        }));
      });

      return abortRequest;
    },
    [FETCH_QUALITY_PROFILE_SCHEMA]: createFetchSchemaHandler(section, '/qualityprofile/schema', createSchemaUrl),
    [SAVE_QUALITY_PROFILE]: createSaveProviderHandler(section, '/qualityprofile'),
    [DELETE_QUALITY_PROFILE]: createRemoveItemHandler(section, '/qualityprofile')
  },

  //
  // Reducers

  reducers: {
    [SET_QUALITY_PROFILE_VALUE]: createSetSettingValueReducer(section),

    [CLONE_QUALITY_PROFILE]: function(state, { payload }) {
      const id = payload.id;
      const newState = getSectionState(state, section);
      const item = newState.items.find((i) => i.id === id);
      const pendingChanges = { ...item, id: 0 };
      delete pendingChanges.id;

      pendingChanges.name = `${pendingChanges.name} - Copy`;
      newState.pendingChanges = pendingChanges;

      return updateSectionState(state, section, newState);
    }
  }

};
