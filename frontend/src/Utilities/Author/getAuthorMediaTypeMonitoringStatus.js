import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';

export function getAuthorMediaTypeMonitoringStatus(author, mediaType) {
  const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, mediaType);
  const effectiveMediaType = rootFolderStatus.mediaType;
  const monitorExisting = effectiveMediaType === 'ebook' ?
    author?.ebookMonitorExisting :
    author?.audiobookMonitorExisting;
  const monitorFuture = effectiveMediaType === 'ebook' ?
    author?.ebookMonitorFuture :
    author?.audiobookMonitorFuture;
  const isConfigured = !!author && rootFolderStatus.hasRootFolder;

  return {
    mediaType: effectiveMediaType,
    isConfigured,
    monitorExisting: monitorExisting ?? 0,
    monitorFuture: monitorFuture === true,
    monitored: isConfigured && ((monitorExisting ?? 0) > 0 || monitorFuture === true)
  };
}

export function getAuthorMonitorExistingValue(author, mediaType) {
  return getAuthorMediaTypeMonitoringStatus(author, mediaType).monitorExisting;
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
