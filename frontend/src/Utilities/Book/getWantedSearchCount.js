import createAjaxRequest from 'Utilities/createAjaxRequest';
import getFilterValue from 'Utilities/Filter/getFilterValue';

export default function getWantedSearchCount(endpoint, props) {
  if (getFilterValue(props.filters, props.selectedFilterKey, 'monitored', false)) {
    return Promise.resolve(props.totalRecords);
  }

  return createAjaxRequest({
    url: `/wanted/${endpoint}`,
    data: { monitored: true, page: 1, pageSize: 1, mediaType: props.selectedMediaType === 'all' ? undefined : props.selectedMediaType }
  }).request.then((result) => result.totalRecords);
}
