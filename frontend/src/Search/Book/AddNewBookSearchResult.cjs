function shouldShowMissingCatalogIdentityHelp(canAdd, localAudiobookBooks, localEbookBooks) {
  if (canAdd) {
    return false;
  }

  const hasLocalAudiobook = Array.isArray(localAudiobookBooks) && localAudiobookBooks.length > 0;
  const hasLocalEbook = Array.isArray(localEbookBooks) && localEbookBooks.length > 0;

  return !hasLocalAudiobook || !hasLocalEbook;
}

module.exports = { shouldShowMissingCatalogIdentityHelp };
