import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';

export function getAuthorMediaTypeMonitoringStatus(author, mediaType) {
  const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, mediaType);
  const effectiveMediaType = rootFolderStatus.mediaType;
  const monitored = effectiveMediaType === 'ebook' ?
    author?.ebookMonitored :
    author?.audiobookMonitored;
  const monitorNewItems = effectiveMediaType === 'ebook' ?
    author?.ebookMonitorNewItems :
    author?.audiobookMonitorNewItems;
  const isConfigured = !!author && rootFolderStatus.hasRootFolder;

  return {
    mediaType: effectiveMediaType,
    isConfigured,
    monitored: isConfigured && monitored === true,
    monitorNewItems: monitorNewItems ?? 'none'
  };
}

export function getAuthorMonitoredValue(author, mediaType) {
  return getAuthorMediaTypeMonitoringStatus(author, mediaType).monitored;
}

export function isAuthorMonitoredForMediaType(author, mediaType) {
  return getAuthorMediaTypeMonitoringStatus(author, mediaType).monitored;
}

export function isAuthorMonitoredForAnyMediaType(author) {
  return isAuthorMonitoredForMediaType(author, 'audiobook') ||
    isAuthorMonitoredForMediaType(author, 'ebook');
}

export function isAuthorMonitoredForSelection(author, selectedMediaType) {
  if (selectedMediaType === 'audiobook' || selectedMediaType === 'ebook') {
    return isAuthorMonitoredForMediaType(author, selectedMediaType);
  }

  return isAuthorMonitoredForAnyMediaType(author);
}
