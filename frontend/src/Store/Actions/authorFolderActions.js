import { createAction } from 'redux-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'authorFolder';

//
// State

export const defaultState = {
  isModalOpen: false,
  authorId: null,
  authorName: null,
  rootFolderId: null,
  matches: []
};

//
// Actions Types

export const SHOW_AUTHOR_FOLDER_PICKER = 'authorFolder/showAuthorFolderPicker';
export const HIDE_AUTHOR_FOLDER_PICKER = 'authorFolder/hideAuthorFolderPicker';

//
// Action Creators

export const showAuthorFolderPicker = createAction(SHOW_AUTHOR_FOLDER_PICKER);
export const hideAuthorFolderPicker = createAction(HIDE_AUTHOR_FOLDER_PICKER);

//
// Action Handlers

export const actionHandlers = handleThunks({
});

//
// Reducers

export const reducers = createHandleActions({

  [SHOW_AUTHOR_FOLDER_PICKER]: (state, { payload }) => {
    return {
      ...state,
      isModalOpen: true,
      authorId: payload.authorId,
      authorName: payload.authorName,
      rootFolderId: payload.rootFolderId,
      matches: payload.matches
    };
  },

  [HIDE_AUTHOR_FOLDER_PICKER]: (state) => {
    return {
      ...state,
      isModalOpen: false,
      authorId: null,
      authorName: null,
      rootFolderId: null,
      matches: []
    };
  }

}, defaultState, section);