
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
    audiobookTags,
    ebookTags,
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

  // Filter settings based on mediaType. Existing/current book selection is an
  // add-time operation; only the author gate and ongoing policy are persisted.
  if (mediaType === 'audiobook') {
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.audiobookMetadataProfileId = audiobookMetadataProfileId || metadataProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;
    author.audiobookMonitored = audiobookMonitored !== false;
    author.audiobookMonitorNewItems = normalizeMonitorNewItemsOption(audiobookMonitorNewItems || monitorNewItems);
    author.audiobookTags = audiobookTags ?? tags;
  } else if (mediaType === 'ebook') {
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.ebookMetadataProfileId = ebookMetadataProfileId || metadataProfileId;
    author.ebookRootFolderPath = ebookRootFolderPath;
    author.ebookMonitored = ebookMonitored !== false;
    author.ebookMonitorNewItems = normalizeMonitorNewItemsOption(ebookMonitorNewItems || monitorNewItems);
    author.ebookTags = ebookTags ?? tags;
  } else {
    author.audiobookQualityProfileId = audiobookQualityProfileId;
    author.audiobookMetadataProfileId = audiobookMetadataProfileId || metadataProfileId;
    author.ebookQualityProfileId = ebookQualityProfileId;
    author.ebookMetadataProfileId = ebookMetadataProfileId || metadataProfileId;
    author.audiobookRootFolderPath = audiobookRootFolderPath;
    author.ebookRootFolderPath = ebookRootFolderPath;
    author.audiobookMonitored = audiobookMonitored !== false;
    author.ebookMonitored = ebookMonitored !== false;
    author.audiobookMonitorNewItems = normalizeMonitorNewItemsOption(audiobookMonitorNewItems || monitorNewItems);
    author.ebookMonitorNewItems = normalizeMonitorNewItemsOption(ebookMonitorNewItems || monitorNewItems);
    author.audiobookTags = audiobookTags ?? tags;
    author.ebookTags = ebookTags ?? tags;
  }

  author.tags = Array.from(new Set([
    ...(author.audiobookTags ?? []),
    ...(author.ebookTags ?? [])
  ]));

  // Keep the legacy aggregate accurate for older consumers while all new
  // monitoring decisions use the explicit per-media gates above.
  author.monitored = author.audiobookMonitored === true || author.ebookMonitored === true;

  return author;
}

export default getNewAuthor;
