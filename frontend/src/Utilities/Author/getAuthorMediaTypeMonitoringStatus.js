import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';

export function getAuthorMonitorExistingValue(author, mediaType) {
  if (!author) {
    return 0;
  }

  const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, mediaType);

  if (!rootFolderStatus.hasRootFolder) {
    return 0;
  }

  const effectiveMediaType = rootFolderStatus.mediaType;
  const monitorExistingValue = effectiveMediaType === 'ebook'
    ? author.ebookMonitorExisting
    : author.audiobookMonitorExisting;

  // NULL means "not configured for this media type yet" → treat as unmonitored.
  return monitorExistingValue ?? 0;
}

export function isAuthorMonitoredForMediaType(author, mediaType) {
  return getAuthorMonitorExistingValue(author, mediaType) > 0;
}

export function isAuthorMonitoredForAnyMediaType(author) {
  if (!author) {
    return false;
  }

  const audiobookStatus = getAuthorMediaTypeRootFolderStatus(author, 'audiobook');
  const ebookStatus = getAuthorMediaTypeRootFolderStatus(author, 'ebook');

  const audiobookMonitored = audiobookStatus.hasRootFolder && (author.audiobookMonitorExisting ?? 0) > 0;
  const ebookMonitored = ebookStatus.hasRootFolder && (author.ebookMonitorExisting ?? 0) > 0;

  return audiobookMonitored || ebookMonitored;
}

