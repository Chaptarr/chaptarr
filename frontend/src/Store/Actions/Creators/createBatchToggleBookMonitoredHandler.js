import updateBooks from 'Utilities/Book/updateBooks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import coerceMonitoredBoolean from 'Utilities/Monitoring/coerceMonitoredBoolean';
import getSectionState from 'Utilities/State/getSectionState';

function createBatchToggleBookMonitoredHandler(section, fetchHandler) {
  return function(getState, payload, dispatch) {
    const {
      bookIds,
      monitored,
      mediaType
    } = payload;

    // Defensive: API expects a boolean, but UI can sometimes pass 0/1/2
    const monitoredBool = coerceMonitoredBoolean(monitored);

    const state = getSectionState(getState(), section, true);
    const selectedIds = new Set(bookIds);
    const selectedMediaTypes = new Set(
      (state?.items || [])
        .filter((book) => selectedIds.has(book.id))
        .map((book) => book.mediaType)
        .filter((value) => value === 'audiobook' || value === 'ebook')
    );
    const effectiveMediaType = mediaType || (selectedMediaTypes.size === 1 ? Array.from(selectedMediaTypes)[0] : null);

    dispatch(updateBooks(section, state.items, bookIds, {
      isSaving: true
    }));

    const promise = createAjaxRequest({
      url: effectiveMediaType ? `/book/monitor?mediaType=${encodeURIComponent(effectiveMediaType)}` : '/book/monitor',
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
