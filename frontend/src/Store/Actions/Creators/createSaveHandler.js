import $ from 'jquery';
import { batchActions } from 'redux-batched-actions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import { set, update } from '../baseActions';

function createSaveHandler(section, url) {
  let pendingSave = null;
  let failedNames = [];

  return function(getState, payload, dispatch, saveOnlyPayload = false) {
    const state = getSectionState(getState(), section, true);
    // Modal saves must not commit drafts owned by another settings surface.
    const changes = { ...(saveOnlyPayload ? payload : { ...state.pendingChanges, ...payload }) };
    const previousSave = pendingSave;
    const saveRequest = $.Deferred();
    pendingSave = saveRequest;

    const remainingChanges = () => Object.fromEntries(Object.entries(getSectionState(getState(), section, true).pendingChanges)
      .filter(([name, value]) => !Object.prototype.hasOwnProperty.call(changes, name) || value !== changes[name]));

    const fail = (xhr, textStatus, errorThrown) => {
      const saveError = xhr instanceof Error ? { status: 0, responseJSON: { message: xhr.message } } : xhr;
      failedNames = Object.keys(changes);
      try {
        dispatch(set({
          section,
          isSaving: false,
          saveError
        }));
      } finally {
        saveRequest.reject(saveError, textStatus, errorThrown);
      }
    };

    const save = () => {
      try {
        dispatch(set({ section, isSaving: true }));
        // Full-resource writes must use the previous save's result, not its stale snapshot.
        const saveData = { ...getSectionState(getState(), section, true).item, ...changes };
        const promise = createAjaxRequest({
          url,
          method: 'PUT',
          dataType: 'json',
          data: JSON.stringify(saveData)
        }).request;

        promise.done((data, textStatus, xhr) => {
          try {
            const pendingChanges = remainingChanges();
            failedNames = failedNames.filter((name) => Object.prototype.hasOwnProperty.call(pendingChanges, name) &&
              !Object.prototype.hasOwnProperty.call(changes, name));
            const saveError = failedNames.length ? getSectionState(getState(), section, true).saveError : null;
            dispatch(batchActions([
              update({ section, data }),

              set({
                section,
                isSaving: false,
                saveError,
                pendingChanges
              })
            ]));
          } finally {
            saveRequest.resolve(data, textStatus, xhr);
          }
        });

        promise.fail(fail);
      } catch (error) {
        fail(error);
      }
    };

    if (previousSave?.state() === 'pending') {
      previousSave.then(save, save);
    } else {
      save();
    }

    return saveRequest.promise();
  };
}

export default createSaveHandler;
