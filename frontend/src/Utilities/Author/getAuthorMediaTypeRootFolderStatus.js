function isNonEmptyString(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function normalizeMediaType(mediaType) {
  return (mediaType || '').toString().toLowerCase() === 'ebook' ? 'ebook' : 'audiobook';
}

export default function getAuthorMediaTypeRootFolderStatus(author, mediaType) {
  const normalizedMediaType = normalizeMediaType(mediaType);

  if (!author) {
    return {
      mediaType: normalizedMediaType,
      isConfigured: false,
      isDataLoading: false,
      hasRootFolder: false
    };
  }

  const audiobookConfigured = isNonEmptyString(author.audiobookRootFolderPath) || isNonEmptyString(author.audiobookFolder);
  const ebookConfigured = isNonEmptyString(author.ebookRootFolderPath) || isNonEmptyString(author.ebookFolder);

  const isConfigured = normalizedMediaType === 'audiobook' ? audiobookConfigured : ebookConfigured;

  const isDataLoading = normalizedMediaType === 'audiobook'
    ? Boolean(author.audiobookQualityProfileId && author.audiobookRootFolderPath === undefined && author.audiobookFolder === undefined)
    : Boolean(author.ebookQualityProfileId && author.ebookRootFolderPath === undefined && author.ebookFolder === undefined);

  return {
    mediaType: normalizedMediaType,
    isConfigured,
    isDataLoading,
    // Treat "loading" as "configured" for UI gating to avoid flicker / false-negative errors.
    hasRootFolder: isDataLoading ? true : isConfigured
  };
}

