import $ from 'jquery';
import { batchActions } from 'redux-batched-actions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { removeItem, set } from '../baseActions';

// allowQueuedResponse: opt-in for endpoints that can respond 202 (queued, not done yet) instead
// of always meaning "already deleted" - e.g. an author delete large enough to run as a background
// command. Left off (default) for every other consumer of this shared factory, whose endpoints
// have never returned anything but a completed 2xx for a delete.
//
// onQueued(dispatch, getState, payload): optional feedback hook for that 202 case, so the user is told the
// delete is happening in the background instead of the page silently staying as it was.
function createRemoveItemHandler(section, url, { allowQueuedResponse = false, onQueued = null } = {}) {
  return function(getState, payload, dispatch) {
    const {
      id,
      queryParams: nestedQueryParams = {},
      ...queryParams
    } = payload;

    dispatch(set({ section, isDeleting: true }));

    const mergedQueryParams = { ...queryParams };

    if (nestedQueryParams && typeof nestedQueryParams === 'object' && !Array.isArray(nestedQueryParams)) {
      Object.assign(mergedQueryParams, nestedQueryParams);
    }

    const queryString = $.param(mergedQueryParams, true);

    const ajaxOptions = {
      url: queryString ? `${url}/${id}?${queryString}` : `${url}/${id}`,
      method: 'DELETE'
    };

    const promise = createAjaxRequest(ajaxOptions).request;

    promise.done((data, textStatus, jqXHR) => {
      // 202 means the delete was only queued (e.g. a large author delete run as a background
      // command instead of inline) - the row hasn't actually been removed yet, so pulling it out
      // of the UI now would show it as gone while it's still fully present in the database.
      // Leave it in place; it'll disappear once the command finishes and the list next refreshes.
      if (allowQueuedResponse && jqXHR.status === 202) {
        dispatch(set({
          section,
          isDeleting: false,
          deleteError: null
        }));

        if (onQueued) {
          onQueued(dispatch, getState, payload);
        }

        return;
      }

      dispatch(batchActions([
        set({
          section,
          isDeleting: false,
          deleteError: null
        }),

        removeItem({ section, id })
      ]));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isDeleting: false,
        deleteError: xhr
      }));
    });

    return promise;
  };
}

export default createRemoveItemHandler;
