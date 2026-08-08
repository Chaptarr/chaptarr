const serverSideFilterKeys = new Set([
  'all',
  'monitored',
  'unmonitored',
  'downloaded',
  'missing',
  'wanted'
]);

export function isServerSideBookIndexFilter(filterKey) {
  return serverSideFilterKeys.has(filterKey);
}

export default function getBookIndexQuery(bookIndex = {}, selectedMediaType) {
  const sortKey = bookIndex.sortKey || 'cleanTitle';
  const sortDirection = bookIndex.sortDirection || 'ascending';
  const filterKey = bookIndex.selectedFilterKey || 'all';
  const mediaType = selectedMediaType || 'audiobook';
  let monitored = undefined;
  let downloaded = undefined;
  let missing = undefined;
  let wanted = undefined;

  if (filterKey === 'monitored') {
    monitored = true;
  } else if (filterKey === 'unmonitored') {
    monitored = false;
  } else if (filterKey === 'downloaded') {
    downloaded = true;
  } else if (filterKey === 'missing') {
    monitored = true;
    missing = true;
  } else if (filterKey === 'wanted') {
    monitored = true;
    wanted = true;
  }

  return {
    queryKey: `${sortKey}_${sortDirection}_${filterKey}_${mediaType}`,
    useClientSidePosters: !isServerSideBookIndexFilter(filterKey),
    queryParams: {
      sortKey,
      sortDirection,
      filters: {
        mediaType,
        monitored,
        downloaded,
        missing,
        wanted
      }
    }
  };
}
