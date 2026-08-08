
function getNewAuthor(author, payload, mediaType) {
  const {
    foreignAuthorId, // Provider-prefixed author ID (e.g., "hc:191785")
    audiobookRootFolderPath,
    ebookRootFolderPath,
    monitor,
    monitorNewItems,
    audiobookQualityProfileId,
    ebookQualityProfileId,
    metadataProfileId,
    audiobookMetadataProfileId,
    ebookMetadataProfileId,
    tags,
    searchForMissingBooks = false
  } = payload;

  const addOptions = {
    monitor,
    searchForMissingBooks
  };

  author.addOptions = addOptions;
  author.monitored = true;

  if (foreignAuthorId) {
    author.foreignAuthorId = foreignAuthorId;
  }

  // Use media-type-specific metadata profile if available, otherwise fall back to generic
  // Values are already extracted in the connector, so use them directly
  if (mediaType === 'audiobook' && audiobookMetadataProfileId) {
    author.metadataProfileId = audiobookMetadataProfileId;
  } else if (mediaType === 'ebook' && ebookMetadataProfileId) {
    author.metadataProfileId = ebookMetadataProfileId;
  } else {
    author.metadataProfileId = metadataProfileId;
  }

  author.tags = tags;

  // Filter settings based on mediaType
  if (mediaType === 'audiobook') {
    // Only set audiobook-related fields
    // Values are already extracted in the connector
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;

    // Tri-state existing monitoring:
    // - all => monitor all existing books
    // - select => monitor no existing books by default (user selects later)
    // - specificBook => select mode + "only this book" context (handled by getNewBook)
    // - anything else => none
    // TRI-STATE MONITORING: 0=None, 1=All, 2=Selected
    const isSpecificBook = monitor === 'specificBook';
    const isSelectMode = monitor === 'select' || isSpecificBook;

    if (isSelectMode) {
      // Author MUST be monitored for this media type to allow individual book monitoring
      author.audiobookMonitorExisting = 2; // Selected mode - monitor specific books
      author.audiobookMonitorFuture = isSpecificBook ? false : (monitorNewItems === 'all');
    } else {
      author.audiobookMonitorExisting = monitor === 'all' ? 1 : 0; // 1=All, 0=None
      author.audiobookMonitorFuture = monitorNewItems === 'all'; // Boolean: monitor future releases
    }
    // Don't set ebook fields to avoid overwriting existing settings
  } else if (mediaType === 'ebook') {
    // Only set ebook-related fields
    // Values are already extracted in the connector
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.ebookRootFolderPath = ebookRootFolderPath;

    // Tri-state existing monitoring:
    // - all => monitor all existing books
    // - select => monitor no existing books by default (user selects later)
    // - specificBook => select mode + "only this book" context (handled by getNewBook)
    // - anything else => none
    // TRI-STATE MONITORING: 0=None, 1=All, 2=Selected
    const isSpecificBook = monitor === 'specificBook';
    const isSelectMode = monitor === 'select' || isSpecificBook;

    if (isSelectMode) {
      // Author MUST be monitored for this media type to allow individual book monitoring
      author.ebookMonitorExisting = 2; // Selected mode - monitor specific books
      author.ebookMonitorFuture = isSpecificBook ? false : (monitorNewItems === 'all');
    } else {
      author.ebookMonitorExisting = monitor === 'all' ? 1 : 0; // 1=All, 0=None
      author.ebookMonitorFuture = monitorNewItems === 'all'; // Boolean: monitor future releases
    }
    // Don't set audiobook fields to avoid overwriting existing settings
  } else {
    // Default behavior - set both (backwards compatible)
    // Values are already extracted in the connector
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;
    author.ebookRootFolderPath = ebookRootFolderPath;
    // TRI-STATE MONITORING: 0=None, 1=All, 2=Selected
    author.audiobookMonitorExisting = monitor === 'all' ? 1 : (monitor === 'select' ? 2 : 0);
    author.audiobookMonitorFuture = monitorNewItems === 'all'; // Boolean: monitor future releases
    author.ebookMonitorExisting = monitor === 'all' ? 1 : (monitor === 'select' ? 2 : 0);
    author.ebookMonitorFuture = monitorNewItems === 'all'; // Boolean: monitor future releases
  }

  return author;
}

export default getNewAuthor;
