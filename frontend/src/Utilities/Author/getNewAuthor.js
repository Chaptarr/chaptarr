
import { normalizeMonitorNewItemsOption } from 'Utilities/Author/monitorNewItemsOptions';

function getNewAuthor(author, payload, mediaType) {
  const {
    foreignAuthorId, // Provider-prefixed author ID (e.g., "hc:191785")
    audiobookRootFolderPath,
    ebookRootFolderPath,
    audiobookMonitored,
    ebookMonitored,
    audiobookMonitorNewItems,
    ebookMonitorNewItems,
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

  // Filter settings based on mediaType. Existing/current book selection is an
  // add-time operation; only the author gate and ongoing policy are persisted.
  if (mediaType === 'audiobook') {
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;
    author.audiobookMonitored = audiobookMonitored !== false;
    author.audiobookMonitorNewItems = normalizeMonitorNewItemsOption(audiobookMonitorNewItems || monitorNewItems);
  } else if (mediaType === 'ebook') {
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.ebookRootFolderPath = ebookRootFolderPath;
    author.ebookMonitored = ebookMonitored !== false;
    author.ebookMonitorNewItems = normalizeMonitorNewItemsOption(ebookMonitorNewItems || monitorNewItems);
  } else {
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;
    author.ebookRootFolderPath = ebookRootFolderPath;
    author.audiobookMonitored = audiobookMonitored !== false;
    author.ebookMonitored = ebookMonitored !== false;
    author.audiobookMonitorNewItems = normalizeMonitorNewItemsOption(audiobookMonitorNewItems || monitorNewItems);
    author.ebookMonitorNewItems = normalizeMonitorNewItemsOption(ebookMonitorNewItems || monitorNewItems);
  }

  // Keep the legacy aggregate accurate for older consumers while all new
  // monitoring decisions use the explicit per-media gates above.
  author.monitored = author.audiobookMonitored === true || author.ebookMonitored === true;

  return author;
}

export default getNewAuthor;
