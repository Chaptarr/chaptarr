import $ from 'jquery';
import _ from 'lodash';
import createAjaxRequest from './createAjaxRequest';

function flattenProviderData(providerData) {
  return _.reduce(Object.keys(providerData), (result, key) => {
    const property = providerData[key];

    if (key === 'fields') {
      result[key] = property;
    } else {
      result[key] = property.value;
    }

    return result;
  }, {});
}

function requestAction(payload) {
  const {
    provider,
    action,
    providerData,
    queryParams,
    timeout
  } = payload;

  const ajaxOptions = {
    url: `/${provider}/action/${action}`,
    contentType: 'application/json',
    method: 'POST',
    data: JSON.stringify(flattenProviderData(providerData))
  };

  if (timeout) {
    ajaxOptions.timeout = timeout;
  }

  if (queryParams) {
    ajaxOptions.url += `?${$.param(queryParams, true)}`;
  }

  return createAjaxRequest(ajaxOptions).request;
}

export default requestAction;
