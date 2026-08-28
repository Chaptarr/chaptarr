import translate from 'Utilities/String/translate';
import { getMediaTypeScopeLabel } from './mediaTypeScopes';

// Folder type constants matching backend enum
export const FolderType = {
  Mixed: 0,
  Audiobook: 1,
  Ebook: 2
};

// Coerce folderType values from the API or UI into the numeric enum used in the frontend.
// Backend serializes enums as strings (e.g. "mixed", "audiobook", "ebook") but will also
// accept numeric values on input.
export function coerceFolderType(folderType) {
  if (folderType == null) {
    return folderType;
  }

  if (typeof folderType === 'number') {
    return folderType;
  }

  if (typeof folderType !== 'string') {
    return folderType;
  }

  const normalized = folderType.trim().toLowerCase();

  // Numeric strings
  if (normalized === '0' || normalized === '1' || normalized === '2') {
    return Number(normalized);
  }

  if (normalized === 'mixed') {
    return FolderType.Mixed;
  }

  if (normalized === 'audiobook' || normalized === 'audio') {
    return FolderType.Audiobook;
  }

  if (normalized === 'ebook' || normalized === 'e-book') {
    return FolderType.Ebook;
  }

  return folderType;
}

// Helper function to get label for folder type
export function getFolderTypeLabel(folderType) {
  const normalizedFolderType = coerceFolderType(folderType);

  switch (normalizedFolderType) {
    case FolderType.Mixed:
      return getMediaTypeScopeLabel('both');
    case FolderType.Audiobook:
      return getMediaTypeScopeLabel('audiobook');
    case FolderType.Ebook:
      return getMediaTypeScopeLabel('ebook');
    default:
      return translate('Unknown');
  }
}

export function getRootFolderMediaTypes(folder) {
  const folderType = coerceFolderType(folder?.folderType);

  if (folderType === FolderType.Audiobook) {
    return ['audiobook'];
  }

  if (folderType === FolderType.Ebook) {
    return ['ebook'];
  }

  if (folderType === FolderType.Mixed) {
    return ['audiobook', 'ebook'];
  }

  // Fallback for legacy rows where per-media settings exist but folderType is unknown.
  const audiobook = folder?.audiobook;
  const ebook = folder?.ebook;
  const mediaTypes = [];

  const hasAudiobookSettings = audiobook && (
    audiobook.qualityProfileId ||
    audiobook.metadataProfileId ||
    audiobook.monitored != null ||
    audiobook.monitorExistingMode != null ||
    audiobook.monitorExistingBooks != null ||
    audiobook.monitorNewItems != null
  );

  const hasEbookSettings = ebook && (
    ebook.qualityProfileId ||
    ebook.metadataProfileId ||
    ebook.monitored != null ||
    ebook.monitorExistingMode != null ||
    ebook.monitorExistingBooks != null ||
    ebook.monitorNewItems != null
  );

  if (hasAudiobookSettings) {
    mediaTypes.push('audiobook');
  }

  if (hasEbookSettings) {
    mediaTypes.push('ebook');
  }

  return mediaTypes;
}

// Helper function to determine what media types a root folder is configured for.
export function getRootFolderMediaTypeScope(folder) {
  const mediaTypes = getRootFolderMediaTypes(folder);

  if (mediaTypes.length === 2) {
    return 'both';
  }

  return mediaTypes[0] ?? null;
}

export function getRootFolderMediaTypeLabel(folder) {
  return getMediaTypeScopeLabel(getRootFolderMediaTypeScope(folder));
}

// Helper function to check if folder accepts a specific media type
export function folderAcceptsMediaType(folderType, mediaType) {
  const normalizedFolderType = coerceFolderType(folderType);

  if (normalizedFolderType === FolderType.Mixed) {
    return true; // Mixed folders accept everything
  }

  // mediaType: 0 = Audiobook, 1 = Ebook (from backend MediaType enum)
  if (normalizedFolderType === FolderType.Audiobook && mediaType === 0) {
    return true;
  }

  if (normalizedFolderType === FolderType.Ebook && mediaType === 1) {
    return true;
  }

  return false;
}
