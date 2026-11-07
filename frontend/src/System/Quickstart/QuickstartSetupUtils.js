import { getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';

export function getMediaTypesForScope(mediaScope) {
  if (mediaScope === 'both') {
    return ['audiobook', 'ebook'];
  }

  return mediaScope ? [mediaScope] : [];
}

export function getConfiguredMediaScope(rootFolders = []) {
  const configuredMediaTypes = new Set(
    rootFolders.flatMap((rootFolder) => getRootFolderMediaTypes(rootFolder))
  );

  if (configuredMediaTypes.has('audiobook') && configuredMediaTypes.has('ebook')) {
    return 'both';
  }

  if (configuredMediaTypes.has('audiobook')) {
    return 'audiobook';
  }

  if (configuredMediaTypes.has('ebook')) {
    return 'ebook';
  }

  return null;
}

export function hasRootFolderForScope(rootFolders = [], mediaScope) {
  const requiredMediaTypes = getMediaTypesForScope(mediaScope);

  if (requiredMediaTypes.length === 0) {
    return false;
  }

  const configuredMediaTypes = new Set(
    rootFolders.flatMap((rootFolder) => getRootFolderMediaTypes(rootFolder))
  );

  return requiredMediaTypes.every((mediaType) => configuredMediaTypes.has(mediaType));
}

export function getQuickstartStatus({ configured, failed, skipped }) {
  if (failed) {
    return 'failed';
  }

  if (configured) {
    return 'configured';
  }

  if (skipped) {
    return 'skipped';
  }

  return 'needed';
}
