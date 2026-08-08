import PropTypes from 'prop-types';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Alert from 'Components/Alert';
import FormInputGroup from 'Components/Form/FormInputGroup';
import SpinnerButton from 'Components/Link/SpinnerButton';
import { inputTypes, kinds } from 'Helpers/Props';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import requestAction from 'Utilities/requestAction';
import translate from 'Utilities/String/translate';
import styles from './EditNotificationModalContent.css';

function getFieldValue(fields, name) {
  return fields.find((field) => field.name === name)?.value;
}

function getSlotKey(rootFolderId, mediaType) {
  return `${rootFolderId}:${mediaType}`;
}

function getMappingKey(mapping) {
  return getSlotKey(mapping.rootFolderId, mapping.mediaType);
}

function buildRootFolderSlots(rootFolders) {
  return (rootFolders || [])
    .flatMap((rootFolder) => {
      const folderType = coerceFolderType(rootFolder.folderType);

      if (folderType === FolderType.Mixed) {
        return [
          { rootFolderId: rootFolder.id, mediaType: 'audiobook', path: rootFolder.path, folderType },
          { rootFolderId: rootFolder.id, mediaType: 'ebook', path: rootFolder.path, folderType }
        ];
      }

      if (folderType === FolderType.Audiobook) {
        return [
          { rootFolderId: rootFolder.id, mediaType: 'audiobook', path: rootFolder.path, folderType }
        ];
      }

      if (folderType === FolderType.Ebook) {
        return [
          { rootFolderId: rootFolder.id, mediaType: 'ebook', path: rootFolder.path, folderType }
        ];
      }

      return [];
    })
    .sort((left, right) => {
      const pathComparison = left.path.localeCompare(right.path);
      if (pathComparison !== 0) {
        return pathComparison;
      }

      return left.mediaType.localeCompare(right.mediaType);
    });
}

function enrichLegacyMapping(rootFolderId, mediaType, libraryId, libraries) {
  const library = libraries.find((candidate) => candidate.id === libraryId);

  if (!library) {
    return {
      rootFolderId,
      mediaType,
      libraryId
    };
  }

  const folder = library.folders?.length === 1 ? library.folders[0] : null;

  return {
    rootFolderId,
    mediaType,
    libraryId,
    libraryFolderId: folder?.id ?? null,
    libraryFolderPath: folder?.fullPath ?? null
  };
}

function deriveLegacyMappings(rootFolders, legacyAudiobookLibraryId, legacyEbookLibraryId, libraries) {
  const slots = buildRootFolderSlots(rootFolders);
  const mappings = [];

  slots.forEach((slot) => {
    const libraryId = slot.mediaType === 'audiobook' ? legacyAudiobookLibraryId : legacyEbookLibraryId;

    if (!libraryId) {
      return;
    }

    mappings.push(enrichLegacyMapping(slot.rootFolderId, slot.mediaType, libraryId, libraries));
  });

  return mappings;
}

function createLibraryOption(library, folder) {
  const libraryMode = library.audiobooksOnly ?
    translate('AbsLibraryModeAudioOnly') :
    translate('AbsLibraryModeAudioAndEbooks');
  const watcherStatus = library.disableWatcher ?
    `, ${translate('AbsWatcherOff')}` :
    '';

  return {
    key: `${library.id}:${folder.id}`,
    value: `${library.name} — ${folder.fullPath} (${libraryMode}${watcherStatus})`,
    libraryId: library.id,
    libraryFolderId: folder.id,
    libraryFolderPath: folder.fullPath
  };
}

function getOptionsForSlot(slot, libraries, mapping, isFetchingLibraries, canLoadLibraries) {
  if (isFetchingLibraries) {
    return [
      { key: '', value: translate('AbsMappingsLoadingLibraries'), isDisabled: true }
    ];
  }

  const matchingLibraries = (libraries || []).filter((library) => {
    if (library.mediaType !== 'book') {
      return false;
    }

    if (slot.mediaType === 'ebook') {
      return !library.audiobooksOnly;
    }

    return true;
  });

  const options = matchingLibraries.flatMap((library) => {
    return (library.folders || []).map((folder) => createLibraryOption(library, folder));
  });

  options.sort((left, right) => left.value.localeCompare(right.value));

  if (mapping?.libraryId && mapping?.libraryFolderId && !options.some((option) => option.key === `${mapping.libraryId}:${mapping.libraryFolderId}`)) {
    options.unshift({
      key: `${mapping.libraryId}:${mapping.libraryFolderId}`,
      value: mapping.libraryFolderPath || mapping.libraryId,
      libraryId: mapping.libraryId,
      libraryFolderId: mapping.libraryFolderId,
      libraryFolderPath: mapping.libraryFolderPath
    });
  }

  if (!options.length && canLoadLibraries) {
    return [
      { key: '', value: translate('AbsMappingsLoadLibrariesFirst'), isDisabled: true }
    ];
  }

  if (!options.length) {
    return [
      { key: '', value: translate('AbsMappingsEnterCredentialsFirst'), isDisabled: true }
    ];
  }

  return [
    { key: '', value: translate('None') },
    ...options
  ];
}

function getErrorMessage(xhr) {
  if (Array.isArray(xhr?.responseJSON) && xhr.responseJSON.length) {
    return xhr.responseJSON[0].errorMessage;
  }

  if (typeof xhr?.responseJSON === 'string' && xhr.responseJSON) {
    return xhr.responseJSON;
  }

  if (xhr?.responseText) {
    return xhr.responseText;
  }

  return translate('AbsMappingsLoadLibrariesError');
}

function normalizeMappings(mappings) {
  return Array.isArray(mappings) ? mappings : [];
}

function sortMappings(mappings) {
  return [...mappings].sort((left, right) => {
    const rootComparison = left.rootFolderId - right.rootFolderId;
    if (rootComparison !== 0) {
      return rootComparison;
    }

    return left.mediaType.localeCompare(right.mediaType);
  });
}

function AudioBookShelfLibraryMappings(props) {
  const {
    item,
    rootFolders,
    isRootFoldersFetching,
    rootFoldersError,
    onInputChange,
    onFieldChange
  } = props;

  const fields = item.fields || [];
  const host = getFieldValue(fields, 'host');
  const port = getFieldValue(fields, 'port');
  const useSsl = getFieldValue(fields, 'useSsl');
  const urlBase = getFieldValue(fields, 'urlBase');
  const apiKey = getFieldValue(fields, 'apiKey');

  const [isFetchingLibraries, setIsFetchingLibraries] = useState(false);
  const [librariesError, setLibrariesError] = useState(null);
  const [libraries, setLibraries] = useState([]);
  const [hasLoadedLibraries, setHasLoadedLibraries] = useState(false);
  const connectionKey = `${host || ''}|${port || ''}|${useSsl === true}|${urlBase || ''}|${apiKey || ''}`;
  const connectionKeyRef = useRef(connectionKey);
  const previousApiKeyRef = useRef(apiKey);

  const canLoadLibraries = !!host && !!port && !!apiKey;

  const loadLibraries = useCallback(() => {
    if (!host || !port || !apiKey) {
      setLibraries([]);
      setLibrariesError(null);
      setIsFetchingLibraries(false);
      setHasLoadedLibraries(false);
      return;
    }

    const requestConnectionKey = connectionKey;
    const request = requestAction({
      provider: 'notification',
      action: 'getDetailedLibraries',
      providerData: item,
      timeout: 10000
    });

    setIsFetchingLibraries(true);
    setLibrariesError(null);

    request.done((data) => {
      if (connectionKeyRef.current !== requestConnectionKey) {
        return;
      }

      setLibraries(data?.libraries || []);
      setIsFetchingLibraries(false);
      setHasLoadedLibraries(true);
    });

    request.fail((xhr) => {
      if (connectionKeyRef.current !== requestConnectionKey) {
        return;
      }

      setLibraries([]);
      setLibrariesError(getErrorMessage(xhr));
      setIsFetchingLibraries(false);
      setHasLoadedLibraries(false);
    });
  }, [apiKey, connectionKey, host, item, port]);

  useEffect(() => {
    connectionKeyRef.current = connectionKey;
    setLibraries([]);
    setLibrariesError(null);
    setIsFetchingLibraries(false);
    setHasLoadedLibraries(false);
  }, [connectionKey]);

  useEffect(() => {
    const previousApiKey = previousApiKeyRef.current;
    previousApiKeyRef.current = apiKey;

    if (!previousApiKey && apiKey && host && port) {
      loadLibraries();
    }
  }, [apiKey, host, loadLibraries, port]);

  const slots = useMemo(() => buildRootFolderSlots(rootFolders), [rootFolders]);
  const slotKeys = useMemo(() => new Set(slots.map((slot) => getSlotKey(slot.rootFolderId, slot.mediaType))), [slots]);
  const configuredMappings = item.audioBookShelfLibraryMappingsConfigured?.value === true;
  const explicitMappings = normalizeMappings(item.audioBookShelfLibraryMappings?.value);
  const hasLegacyLibrarySelection = !!item.legacyAudiobookLibraryId?.value || !!item.legacyEbookLibraryId?.value;

  const effectiveMappings = useMemo(() => {
    if (configuredMappings) {
      return explicitMappings;
    }

    return deriveLegacyMappings(
      rootFolders,
      item.legacyAudiobookLibraryId?.value,
      item.legacyEbookLibraryId?.value,
      libraries
    );
  }, [configuredMappings, explicitMappings, rootFolders, item.legacyAudiobookLibraryId?.value, item.legacyEbookLibraryId?.value, libraries]);

  const visibleMappings = useMemo(() => {
    return effectiveMappings.filter((mapping) => slotKeys.has(getMappingKey(mapping)));
  }, [effectiveMappings, slotKeys]);

  const hiddenMappings = useMemo(() => {
    return explicitMappings.filter((mapping) => !slotKeys.has(getMappingKey(mapping)));
  }, [explicitMappings, slotKeys]);

  const mappingBySlot = useMemo(() => {
    return new Map(visibleMappings.map((mapping) => [getMappingKey(mapping), mapping]));
  }, [visibleMappings]);

  const handleMappingChange = (slot, selectedValue, options) => {
    const nextMappings = visibleMappings.filter((mapping) => getMappingKey(mapping) !== getSlotKey(slot.rootFolderId, slot.mediaType));

    if (selectedValue) {
      const selectedOption = options.find((option) => option.key === selectedValue);

      if (selectedOption) {
        nextMappings.push({
          rootFolderId: slot.rootFolderId,
          mediaType: slot.mediaType,
          libraryId: selectedOption.libraryId,
          libraryFolderId: selectedOption.libraryFolderId,
          libraryFolderPath: selectedOption.libraryFolderPath
        });
      }
    }

    const mergedMappings = sortMappings([...hiddenMappings, ...nextMappings]);

    onInputChange({
      name: 'audioBookShelfLibraryMappings',
      value: mergedMappings
    });

    onInputChange({
      name: 'audioBookShelfLibraryMappingsConfigured',
      value: true
    });

    onFieldChange({
      name: 'libraryMappingsJson',
      value: JSON.stringify(mergedMappings)
    });
  };

  if (isRootFoldersFetching && !rootFolders.length) {
    return (
      <Alert kind={kinds.INFO}>
        {translate('AbsMappingsLoadingRootFolders')}
      </Alert>
    );
  }

  if (rootFoldersError && !rootFolders.length) {
    return (
      <Alert kind={kinds.DANGER}>
        {translate('AbsMappingsRootFoldersError')}
      </Alert>
    );
  }

  if (!slots.length) {
    return (
      <Alert kind={kinds.WARNING}>
        {translate('AbsMappingsNoRootFolders')}
      </Alert>
    );
  }

  return (
    <div className={styles.absMappings}>
      <div className={styles.absMappingToolbar}>
        <div>
          <div className={styles.absMappingTitle}>
            {translate('AbsMappingsTitle')}
          </div>

          <div className={styles.absMappingHelp}>
            {translate('AbsMappingsHelpText')}
          </div>

          <div className={styles.absMappingHelp}>
            {translate('AbsMappingsWatcherRequirement')}
          </div>
        </div>

        <SpinnerButton
          isSpinning={isFetchingLibraries}
          isDisabled={!canLoadLibraries}
          onPress={loadLibraries}
        >
          {translate('AbsMappingsLoadLibraries')}
        </SpinnerButton>
      </div>

      {
        librariesError ?
          <Alert kind={kinds.DANGER}>
            {librariesError}
          </Alert> :
          null
      }

      {
        !configuredMappings && hasLegacyLibrarySelection ?
          <Alert kind={kinds.INFO}>
            {translate('AbsMappingsLegacyWarning')}
          </Alert> :
          null
      }

      <div className={styles.absMappingGridHeader}>
        <div>{translate('AbsMappingsChaptarrRootHeader')}</div>
        <div />
        <div>{translate('AbsMappingsAbsLibraryHeader')}</div>
      </div>

      {
        slots.map((slot) => {
          const slotLabel = slot.mediaType === 'audiobook' ?
            translate('AbsMappingsAudioSlotLabel') :
            translate('AbsMappingsEbookSlotLabel');
          const slotKey = getSlotKey(slot.rootFolderId, slot.mediaType);
          const mapping = mappingBySlot.get(slotKey);
          const options = getOptionsForSlot(slot, libraries, mapping, isFetchingLibraries, canLoadLibraries);
          const selectedValue = mapping?.libraryFolderId && mapping?.libraryId ?
            `${mapping.libraryId}:${mapping.libraryFolderId}` :
            '';

          return (
            <div
              key={slotKey}
              className={styles.absMappingRow}
            >
              <div className={styles.absMappingSource}>
                <span className={styles.absMappingBadge}>
                  {slotLabel}
                </span>

                <span className={styles.absMappingPath}>
                  {slot.path}
                </span>
              </div>

              <div className={styles.absMappingArrow}>
                {'→'}
              </div>

              <div className={styles.absMappingDestination}>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name={slotKey}
                  value={selectedValue}
                  values={options}
                  isDisabled={isFetchingLibraries || !canLoadLibraries || (!hasLoadedLibraries && !selectedValue)}
                  onChange={({ value }) => handleMappingChange(slot, value, options)}
                />
              </div>
            </div>
          );
        })
      }
    </div>
  );
}

AudioBookShelfLibraryMappings.propTypes = {
  item: PropTypes.object.isRequired,
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  isRootFoldersFetching: PropTypes.bool,
  rootFoldersError: PropTypes.object,
  onInputChange: PropTypes.func.isRequired,
  onFieldChange: PropTypes.func.isRequired
};

export default AudioBookShelfLibraryMappings;
