import translate from 'Utilities/String/translate';

export const mediaTypeScopes = {
  BOTH: 'both',
  AUDIOBOOK: 'audiobook',
  EBOOK: 'ebook'
};

export function normalizeMediaTypeScope(value) {
  if (typeof value === 'number') {
    if (value === 0) {
      return mediaTypeScopes.BOTH;
    }

    if (value === 1) {
      return mediaTypeScopes.AUDIOBOOK;
    }

    if (value === 2) {
      return mediaTypeScopes.EBOOK;
    }
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();

    if (['0', 'all', 'both', 'mixed'].includes(normalized)) {
      return mediaTypeScopes.BOTH;
    }

    if (['1', 'audio', 'audiobook'].includes(normalized)) {
      return mediaTypeScopes.AUDIOBOOK;
    }

    if (['2', 'e-book', 'ebook'].includes(normalized)) {
      return mediaTypeScopes.EBOOK;
    }
  }

  return null;
}

export function getMediaTypeScopeLabel(value) {
  if (value == null) {
    return translate('NotConfigured');
  }

  const scope = normalizeMediaTypeScope(value);

  if (scope === mediaTypeScopes.AUDIOBOOK) {
    return translate('AudiobooksOnly');
  }

  if (scope === mediaTypeScopes.EBOOK) {
    return translate('EbooksOnly');
  }

  if (scope === mediaTypeScopes.BOTH) {
    return translate('AudiobooksAndEbooks');
  }

  return translate('Unknown');
}

export function getImportListMediaTypeScope(fields) {
  const audiobookField = (fields || []).find((field) => field.name === 'monitorAudiobooks');
  const ebookField = (fields || []).find((field) => field.name === 'monitorEbooks');
  const includesAudiobooks = audiobookField ? audiobookField.value !== false : !ebookField;
  const includesEbooks = ebookField ? ebookField.value !== false : false;

  if (includesAudiobooks && includesEbooks) {
    return mediaTypeScopes.BOTH;
  }

  return includesEbooks ? mediaTypeScopes.EBOOK : mediaTypeScopes.AUDIOBOOK;
}
