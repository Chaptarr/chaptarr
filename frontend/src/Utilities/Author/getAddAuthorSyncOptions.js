import { supportsAuthorMediaType } from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import { resolveMonitorNewItemsOptionValue } from 'Utilities/Author/monitorNewItemsOptions';
import { resolveMonitorOptionValue } from 'Utilities/Author/monitorOptions';
import translate from 'Utilities/String/translate';

function sharedValue(values, override) {
  return override ?? (values.every((value) => value === values[0]) ? values[0] : null);
}

// Preview roots are not author configuration. Only selected sides and saved
// local sides participate in eligibility and author-level defaults.
export default function getAddAuthorSyncOptions(settings, rootFolders, selectedMediaType, existingAuthor, overrides = {}) {
  const mediaTypes = ['audiobook', 'ebook'];
  const requested = mediaTypes.filter((mediaType) => selectedMediaType === 'both' || selectedMediaType === mediaType);
  const paths = Object.fromEntries(mediaTypes.map((mediaType) => [mediaType,
    requested.includes(mediaType) ? settings[`${mediaType}RootFolderPath`]?.value : existingAuthor?.[`${mediaType}RootFolderPath`]
  ]));
  const canSyncMonitoredAcrossFormats = mediaTypes.every((mediaType) => supportsAuthorMediaType(rootFolders, paths[mediaType], mediaType));
  const rootDefaults = mediaTypes.map((mediaType) => rootFolders.find((folder) => folder.path === paths[mediaType])?.defaultSyncMonitoredAcrossFormats)
    .filter((value) => typeof value === 'boolean');
  const inheritedSync = rootDefaults.length ? sharedValue(rootDefaults) : false;
  const savedSync = existingAuthor?.syncMonitoredAcrossFormats;
  const syncValue = canSyncMonitoredAcrossFormats ?
    (overrides.syncMonitoredAcrossFormats ?? savedSync ?? inheritedSync) : false;
  const initialValues = requested.map((mediaType) => resolveMonitorOptionValue(settings[`${mediaType}Monitor`]?.value, settings.monitor?.value));
  const ongoingValues = mediaTypes.filter((mediaType) => requested.includes(mediaType) || supportsAuthorMediaType(rootFolders, paths[mediaType], mediaType))
    .map((mediaType) => resolveMonitorNewItemsOptionValue(
      requested.includes(mediaType) ? settings[`${mediaType}MonitorNewItems`]?.value : existingAuthor?.[`${mediaType}MonitorNewItems`], settings.monitorNewItems?.value
    ));
  const monitor = sharedValue(initialValues, overrides.sharedMonitor);
  const monitorNewItems = sharedValue(ongoingValues, overrides.sharedMonitorNewItems);
  const field = (value, message) => ({ value: value ?? '', errors: value == null ? [{ message }] : [] });

  return {
    canSyncMonitoredAcrossFormats,
    syncMonitoredAcrossFormats: {
      value: syncValue == null ? 'mixed' : syncValue,
      errors: syncValue == null ? [{ message: translate('AuthorSyncRootDefaultsConflict') }] : []
    },
    sharedMonitor: field(monitor, translate('AuthorSyncInitialMonitoringConflict')),
    sharedMonitorNewItems: field(monitorNewItems, translate('AuthorSyncNewMonitoringConflict')),
    hasUnresolvedSyncOptions: syncValue == null || (syncValue === true && (monitor == null || monitorNewItems == null))
  };
}
