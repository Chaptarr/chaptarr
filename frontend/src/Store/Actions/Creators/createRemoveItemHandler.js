import $ from 'jquery';
import { batchActions } from 'redux-batched-actions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { removeItem, set } from '../baseActions';

function createRemoveItemHandler(section, url) {
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
      if (jqXHR.status === 202) {
        dispatch(set({
          section,
          isDeleting: false,
          deleteError: null
        }));

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
