import { createSelector } from 'reselect';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import { normalizeMonitorNewItemsOption } from 'Utilities/Author/monitorNewItemsOptions';
import { resolveMonitorOption } from 'Utilities/Author/monitorOptions';

export const rootDerivedAuthorDefaults = {
  audiobookMonitored: null,
  audiobookMonitor: null,
  audiobookMonitorNewItems: null,
  audiobookQualityProfileId: 0,
  audiobookMetadataProfileId: 0,
  audiobookTags: null,
  ebookMonitored: null,
  ebookMonitor: null,
  ebookMonitorNewItems: null,
  ebookQualityProfileId: 0,
  ebookMetadataProfileId: 0,
  ebookTags: null
};

export function getRootDerivedDefaultsForMediaType(mediaType) {
  const prefix = mediaType === 'ebook' ? 'ebook' : 'audiobook';

  return Object.fromEntries(
    Object.entries(rootDerivedAuthorDefaults)
      .filter(([key]) => key.startsWith(prefix))
  );
}

export function applyRootFolderMediaDefaults(settings, rootFolders) {
  const availableRootFolders = rootFolders || [];

  const audiobookRoot = settings.audiobookRootFolderPath?.value;
  const ebookRoot = settings.ebookRootFolderPath?.value;

  const audiobookRootFolder = availableRootFolders.find((f) => f.path === audiobookRoot);
  const ebookRootFolder = availableRootFolders.find((f) => f.path === ebookRoot);

  // Inherit defaults from the selected root folder (per media type). Users can override by changing the fields.
  if (settings.audiobookMonitor?.value == null && audiobookRootFolder) {
    settings.audiobookMonitor = {
      ...(settings.audiobookMonitor || {}),
      value: resolveMonitorOption(
        audiobookRootFolder.audiobookMonitorExistingMode,
        audiobookRootFolder.audiobookMonitorExistingBooks
      )
    };
  }

  if (settings.audiobookMonitored?.value == null && audiobookRootFolder) {
    settings.audiobookMonitored = {
      value: audiobookRootFolder.audiobookMonitored !== false
    };
  }

  if (settings.audiobookMonitorNewItems?.value == null && audiobookRootFolder) {
    settings.audiobookMonitorNewItems = {
      ...(settings.audiobookMonitorNewItems || {}),
      value: normalizeMonitorNewItemsOption(audiobookRootFolder.audiobookMonitorNewItems)
    };
  }

  if (settings.ebookMonitor?.value == null && ebookRootFolder) {
    settings.ebookMonitor = {
      ...(settings.ebookMonitor || {}),
      value: resolveMonitorOption(
        ebookRootFolder.ebookMonitorExistingMode,
        ebookRootFolder.ebookMonitorExistingBooks
      )
    };
  }

  if (settings.ebookMonitored?.value == null && ebookRootFolder) {
    settings.ebookMonitored = {
      value: ebookRootFolder.ebookMonitored !== false
    };
  }

  if (settings.ebookMonitorNewItems?.value == null && ebookRootFolder) {
    settings.ebookMonitorNewItems = {
      ...(settings.ebookMonitorNewItems || {}),
      value: normalizeMonitorNewItemsOption(ebookRootFolder.ebookMonitorNewItems)
    };
  }

  const audiobookQualityFromRootFolder = audiobookRootFolder?.audiobookQualityProfileId || audiobookRootFolder?.audiobook?.qualityProfileId;
  const audiobookMetadataFromRootFolder = audiobookRootFolder?.audiobookMetadataProfileId || audiobookRootFolder?.audiobook?.metadataProfileId;
  const ebookQualityFromRootFolder = ebookRootFolder?.ebookQualityProfileId || ebookRootFolder?.ebook?.qualityProfileId;
  const ebookMetadataFromRootFolder = ebookRootFolder?.ebookMetadataProfileId || ebookRootFolder?.ebook?.metadataProfileId;
  const audiobookTagsFromRootFolder = audiobookRootFolder?.audiobookTags || audiobookRootFolder?.audiobook?.tags;
  const ebookTagsFromRootFolder = ebookRootFolder?.ebookTags || ebookRootFolder?.ebook?.tags;

  // Inherit profile defaults from the selected root folder when unset.
  if ((!settings.audiobookQualityProfileId || settings.audiobookQualityProfileId.value === 0) && audiobookQualityFromRootFolder) {
    settings.audiobookQualityProfileId = { ...(settings.audiobookQualityProfileId || {}), value: audiobookQualityFromRootFolder };
  }

  if ((!settings.audiobookMetadataProfileId || settings.audiobookMetadataProfileId.value === 0) && audiobookMetadataFromRootFolder) {
    settings.audiobookMetadataProfileId = { ...(settings.audiobookMetadataProfileId || {}), value: audiobookMetadataFromRootFolder };
  }

  if ((!settings.ebookQualityProfileId || settings.ebookQualityProfileId.value === 0) && ebookQualityFromRootFolder) {
    settings.ebookQualityProfileId = { ...(settings.ebookQualityProfileId || {}), value: ebookQualityFromRootFolder };
  }

  if ((!settings.ebookMetadataProfileId || settings.ebookMetadataProfileId.value === 0) && ebookMetadataFromRootFolder) {
    settings.ebookMetadataProfileId = { ...(settings.ebookMetadataProfileId || {}), value: ebookMetadataFromRootFolder };
  }

  // Inherit tag defaults from the selected root folder when unset.
  if (settings.audiobookTags?.value == null && audiobookTagsFromRootFolder != null) {
    settings.audiobookTags = { ...(settings.audiobookTags || {}), value: audiobookTagsFromRootFolder };
  }

  if (settings.ebookTags?.value == null && ebookTagsFromRootFolder != null) {
    settings.ebookTags = { ...(settings.ebookTags || {}), value: ebookTagsFromRootFolder };
  }

  // Keep the tag input and submit payload on one null-resolution rule.
  // A configured root wins above; otherwise retain the legacy saved tag
  // preference. Empty arrays remain an explicit "no tags" selection.
  if (settings.audiobookTags?.value == null) {
    settings.audiobookTags = {
      ...(settings.audiobookTags || {}),
      value: settings.tags?.value ?? []
    };
  }

  if (settings.ebookTags?.value == null) {
    settings.ebookTags = {
      ...(settings.ebookTags || {}),
      value: settings.tags?.value ?? []
    };
  }
}

function createRootFolderDefaultsSelector() {
  return createSelector(
    (state) => state.settings.rootFolders.items,
    (rootFolders) => {
      const audiobookFolder = rootFolders.find((folder) => folder.isEffectiveDefaultAudiobook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Audiobook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Mixed);
      const ebookFolder = rootFolders.find((folder) => folder.isEffectiveDefaultEbook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Ebook) ||
        rootFolders.find((folder) => coerceFolderType(folder.folderType) === FolderType.Mixed);

      return {
        audiobookRootFolderPath: audiobookFolder ? audiobookFolder.path : '',
        ebookRootFolderPath: ebookFolder ? ebookFolder.path : ''
      };
    }
  );
}

export default createRootFolderDefaultsSelector;
