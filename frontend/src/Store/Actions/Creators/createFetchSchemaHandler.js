import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set } from '../baseActions';

function createFetchSchemaHandler(section, url, createUrl) {
  return function(getState, payload, dispatch) {
    dispatch(set({ section, isSchemaFetching: true }));

    const requestUrl = createUrl ? createUrl(payload) : url;
    const promise = createAjaxRequest({
      url: requestUrl
    }).request;

    promise.done((data) => {
      dispatch(set({
        section,
        isSchemaFetching: false,
        isSchemaPopulated: true,
        schemaError: null,
        schema: data
      }));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isSchemaFetching: false,
        isSchemaPopulated: true,
        schemaError: xhr
      }));
    });
  };
}

export default createFetchSchemaHandler;
