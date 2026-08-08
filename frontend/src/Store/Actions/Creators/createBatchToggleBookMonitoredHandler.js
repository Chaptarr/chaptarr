import updateBooks from 'Utilities/Book/updateBooks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import coerceMonitoredBoolean from 'Utilities/Monitoring/coerceMonitoredBoolean';
import getSectionState from 'Utilities/State/getSectionState';

function createBatchToggleBookMonitoredHandler(section, fetchHandler) {
  return function(getState, payload, dispatch) {
    const {
      bookIds,
      monitored
    } = payload;
    
    // Defensive: API expects a boolean, but UI can sometimes pass 0/1/2
    const monitoredBool = coerceMonitoredBoolean(monitored);

    const state = getSectionState(getState(), section, true);

    dispatch(updateBooks(section, state.items, bookIds, {
      isSaving: true
    }));

    const promise = createAjaxRequest({
      url: '/book/monitor',
      method: 'PUT',
      data: JSON.stringify({ bookIds, monitored: monitoredBool }),
      dataType: 'json'
    }).request;

    promise.done(() => {
      dispatch(updateBooks(section, state.items, bookIds, {
        isSaving: false,
        monitored: monitoredBool
      }));

      dispatch(fetchHandler());
    });

    promise.fail(() => {
      dispatch(updateBooks(section, state.items, bookIds, {
        isSaving: false
      }));
    });
  };
}

export default createBatchToggleBookMonitoredHandler;
