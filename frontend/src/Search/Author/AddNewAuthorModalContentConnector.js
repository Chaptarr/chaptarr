import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { addAuthor, resetAddState, setAuthorAddDefault } from 'Store/Actions/searchActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createRootFolderDefaultsSelector from 'Store/Selectors/createRootFolderDefaultsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import { resolveMonitorNewItemsOptionValue } from 'Utilities/Author/monitorNewItemsOptions';
import { resolveMonitorOptionValue } from 'Utilities/Author/monitorOptions';
import AddNewAuthorModalContent from './AddNewAuthorModalContent';

const rootDerivedAuthorDefaults = {
  audiobookMonitored: null,
  audiobookMonitor: null,
  audiobookMonitorNewItems: null,
  audiobookQualityProfileId: 0,
  audiobookMetadataProfileId: 0,
  ebookMonitored: null,
  ebookMonitor: null,
  ebookMonitorNewItems: null,
  ebookQualityProfileId: 0,
  ebookMetadataProfileId: 0
};

function getRootDerivedDefaultsForMediaType(mediaType) {
  const prefix = mediaType === 'ebook' ? 'ebook' : 'audiobook';

  return Object.fromEntries(
    Object.entries(rootDerivedAuthorDefaults)
      .filter(([key]) => key.startsWith(prefix))
  );
}

function normalizeDefaultMediaType(value, allowBoth) {
  const normalized = (value ?? '').trim().toLowerCase();

  if (normalized === 'audiobook' || normalized === 'ebook') {
    return normalized;
  }

  if (allowBoth && normalized === 'both') {
    return normalized;
  }

  return null;
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.search,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.qualityProfiles,
    (state) => state.settings.rootFolders,
    (state) => state.settings.ui,
    createRootFolderDefaultsSelector(),
    createDimensionsSelector(),
    createSystemStatusSelector(),
    (searchState, metadataProfiles, qualityProfiles, rootFoldersState, uiState, rootFolderDefaults, dimensions, systemStatus) => {
      const {
        isAdding,
        isAdded,
        isQueued,
        addNotice,
        addError,
        authorDefaults
      } = searchState;

      const {
        settings,
        validationErrors,
        validationWarnings
      } = selectSettings(authorDefaults, {}, addError);

      // Apply smart defaults if root folders are empty
      if (rootFoldersState.isPopulated) {
        if (!settings.audiobookRootFolderPath || !settings.audiobookRootFolderPath.value) {
          settings.audiobookRootFolderPath = {
            ...(settings.audiobookRootFolderPath || {}),
            value: rootFolderDefaults.audiobookRootFolderPath
          };
        }
        if (!settings.ebookRootFolderPath || !settings.ebookRootFolderPath.value) {
          settings.ebookRootFolderPath = {
            ...(settings.ebookRootFolderPath || {}),
            value: rootFolderDefaults.ebookRootFolderPath
          };
        }

        const rootFolders = rootFoldersState.items || [];

        const audiobookRoot = settings.audiobookRootFolderPath?.value;
        const ebookRoot = settings.ebookRootFolderPath?.value;

        const audiobookRootFolder = rootFolders.find((f) => f.path === audiobookRoot);
        const ebookRootFolder = rootFolders.find((f) => f.path === ebookRoot);

        const monitorExistingToKey = (monitorExistingMode, monitorExistingBooks) => {
          const normalizedMode = monitorExistingMode?.toString().toLowerCase();
          return ['all', 'missing', 'existing', 'none'].includes(normalizedMode) ?
            normalizedMode :
            (monitorExistingBooks === true ? 'all' : 'none');
        };
        const monitorNewItemsToKey = (value) => {
          const normalized = (value ?? '').toString().trim().toLowerCase();
          return ['all', 'new', 'none'].includes(normalized) ? normalized : 'none';
        };

        // Inherit defaults from the selected root folder (per media type). Users can override by changing the fields.
        if (settings.audiobookMonitor?.value == null && audiobookRootFolder) {
          settings.audiobookMonitor = {
            ...(settings.audiobookMonitor || {}),
            value: monitorExistingToKey(
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
            value: monitorNewItemsToKey(audiobookRootFolder.audiobookMonitorNewItems)
          };
        }

        if (settings.ebookMonitor?.value == null && ebookRootFolder) {
          settings.ebookMonitor = {
            ...(settings.ebookMonitor || {}),
            value: monitorExistingToKey(
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
            value: monitorNewItemsToKey(ebookRootFolder.ebookMonitorNewItems)
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
        if (!settings.audiobookTags && audiobookTagsFromRootFolder != null) {
          settings.audiobookTags = { ...(settings.audiobookTags || {}), value: audiobookTagsFromRootFolder };
        }

        if (!settings.ebookTags && ebookTagsFromRootFolder != null) {
          settings.ebookTags = { ...(settings.ebookTags || {}), value: ebookTagsFromRootFolder };
        }
      }

      // Set quality profile defaults to first available instead of "None"
      if (qualityProfiles.isPopulated && qualityProfiles.items.length > 0) {
        const audiobookProfiles = qualityProfiles.items.filter((p) =>
          p.profileType === 1 || p.profileType === 'audiobook'
        );
        const ebookProfiles = qualityProfiles.items.filter((p) =>
          p.profileType === 2 || p.profileType === 'ebook'
        );

        if ((!settings.audiobookQualityProfileId || settings.audiobookQualityProfileId.value === 0) && audiobookProfiles.length > 0) {
          settings.audiobookQualityProfileId = { ...(settings.audiobookQualityProfileId || {}), value: audiobookProfiles[0].id };
        }

        if ((!settings.ebookQualityProfileId || settings.ebookQualityProfileId.value === 0) && ebookProfiles.length > 0) {
          settings.ebookQualityProfileId = { ...(settings.ebookQualityProfileId || {}), value: ebookProfiles[0].id };
        }
      }

      // Set metadata profile defaults to first available instead of "None"
      if (metadataProfiles.isPopulated && metadataProfiles.items.length > 0) {
        const audiobookMetaProfiles = metadataProfiles.items.filter((p) =>
          p.profileType === 1 || p.profileType === 'audiobook'
        );
        const ebookMetaProfiles = metadataProfiles.items.filter((p) =>
          p.profileType === 2 || p.profileType === 'ebook'
        );

        if ((!settings.audiobookMetadataProfileId || settings.audiobookMetadataProfileId.value === 0) && audiobookMetaProfiles.length > 0) {
          settings.audiobookMetadataProfileId = { ...(settings.audiobookMetadataProfileId || {}), value: audiobookMetaProfiles[0].id };
        }

        if ((!settings.ebookMetadataProfileId || settings.ebookMetadataProfileId.value === 0) && ebookMetaProfiles.length > 0) {
          settings.ebookMetadataProfileId = { ...(settings.ebookMetadataProfileId || {}), value: ebookMetaProfiles[0].id };
        }
      }

      // Prepare success messages for the form (e.g., queued notice)
      const successMessages = [];
      if (isQueued && addNotice) {
        successMessages.push(addNotice);
      }

      return {
        isAdding,
        isAdded,
        isQueued,
        successMessages,
        addError,
        defaultMediaType: uiState.item?.addNewDefaultMediaType,
        isSavingDefaultMediaType: uiState.isSaving,
        uiSaveError: uiState.saveError,
        showMetadataProfile: metadataProfiles.items.length > 2, // NONE (not allowed for authors) and one other
        isSmallScreen: dimensions.isSmallScreen,
        validationErrors,
        validationWarnings,
        isWindows: systemStatus.isWindows,
        rootFoldersPopulated: rootFoldersState.isPopulated,
        ...settings
      };
    }
  );
}

const mapDispatchToProps = {
  setAuthorAddDefault,
  addAuthor,
  fetchRootFolders,
  resetAddState,
  setUISettingsValue,
  saveUISettings,
  showMessage
};

class AddNewAuthorModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      selectedMediaType: normalizeDefaultMediaType(props.defaultMediaType, true) ?? 'audiobook'
    };

    this._didUserSelectMediaType = false;
    this._pendingDefaultSave = false;
  }

  //
  // Lifecycle

  componentDidMount() {
    // Root folders are the default source for each new author. Clear any
    // browser-persisted per-author overrides when this modal opens; choices
    // made after this point apply only to this add.
    this.props.setAuthorAddDefault(rootDerivedAuthorDefaults);

    // Ensure root folders are loaded
    if (!this.props.rootFoldersPopulated) {
      this.props.fetchRootFolders();
    }
  }

  componentDidUpdate(prevProps) {
    // Close modal when author is successfully added
    if (!prevProps.isAdded && this.props.isAdded) {
      this.props.onModalClose();
      // Reset add-state flags without clearing the search results list.
      this.props.resetAddState();
    }

    if (!this._didUserSelectMediaType) {
      const prevDefault = normalizeDefaultMediaType(prevProps.defaultMediaType, true);
      const currDefault = normalizeDefaultMediaType(this.props.defaultMediaType, true);

      if (prevDefault !== currDefault && currDefault) {
        this.setState({ selectedMediaType: currDefault });
      }
    }

    if (this._pendingDefaultSave && prevProps.isSavingDefaultMediaType && !this.props.isSavingDefaultMediaType) {
      if (!this.props.uiSaveError) {
        this.props.showMessage({
          id: `add-new-default-media-type-saved-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaved',
          message: 'Saved default media type',
          type: 'success',
          hideAfter: 5
        });
      } else {
        this.props.showMessage({
          id: `add-new-default-media-type-save-failed-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaveFailed',
          message: this.props.uiSaveError?.responseJSON?.message || 'Unable to save default media type',
          type: 'error',
          hideAfter: 8
        });
      }

      this._pendingDefaultSave = false;
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    if (name === 'audiobookRootFolderPath') {
      this.props.setAuthorAddDefault({
        ...getRootDerivedDefaultsForMediaType('audiobook'),
        audiobookRootFolderPath: value
      });
      return;
    }

    if (name === 'ebookRootFolderPath') {
      this.props.setAuthorAddDefault({
        ...getRootDerivedDefaultsForMediaType('ebook'),
        ebookRootFolderPath: value
      });
      return;
    }

    this.props.setAuthorAddDefault({ [name]: value });
  };

  onMediaTypeChange = (mediaType) => {
    this._didUserSelectMediaType = true;
    this.setState({ selectedMediaType: mediaType });
  };

  onSetDefaultMediaType = (mediaType) => {
    this._pendingDefaultSave = true;
    this.props.setUISettingsValue({ name: 'addNewDefaultMediaType', value: mediaType });
    this.props.saveUISettings();
  };

  onAddAuthorPress = (searchForMissingBooks) => {
    const {
      foreignAuthorId,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      monitor,
      audiobookMonitored,
      ebookMonitored,
      audiobookMonitor,
      ebookMonitor,
      monitorNewItems,
      audiobookMonitorNewItems,
      ebookMonitorNewItems,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      metadataProfileId,
      tags
    } = this.props;

    const { selectedMediaType } = this.state;
    const addAudiobooks = selectedMediaType === 'audiobook' || selectedMediaType === 'both';
    const addEbooks = selectedMediaType === 'ebook' || selectedMediaType === 'both';

    // Validate only the media side(s) the user is actually adding. Values shown
    // on another visited tab are previews and must not make this add valid.
    const audiobookProfile = audiobookQualityProfileId?.value;
    const ebookProfile = ebookQualityProfileId?.value;
    const audiobookProfileMissing = !audiobookProfile || audiobookProfile === 'none';
    const ebookProfileMissing = !ebookProfile || ebookProfile === 'none';

    if ((addAudiobooks && audiobookProfileMissing) ||
        (addEbooks && ebookProfileMissing)) {
      // This should be handled by validation, but as a safety check
      console.error('A quality profile must be selected for each media type being added');
      return;
    }

    // The UI stores media-type-specific monitor values under audiobookMonitor/ebookMonitor.
    // Fall back to the legacy monitor/monitorNewItems fields when the specific ones haven't been touched.
    const audiobookMonitorValue = resolveMonitorOptionValue(audiobookMonitor?.value, monitor?.value);
    const ebookMonitorValue = resolveMonitorOptionValue(ebookMonitor?.value, monitor?.value);

    const audiobookMonitorNewItemsValue = resolveMonitorNewItemsOptionValue(audiobookMonitorNewItems?.value, monitorNewItems?.value);
    const ebookMonitorNewItemsValue = resolveMonitorNewItemsOptionValue(ebookMonitorNewItems?.value, monitorNewItems?.value);

    let selectedMonitor = audiobookMonitorValue;
    let selectedMonitorNewItems = audiobookMonitorNewItemsValue;

    if (selectedMediaType === 'ebook') {
      selectedMonitor = ebookMonitorValue;
      selectedMonitorNewItems = ebookMonitorNewItemsValue;
    }

    this.props.addAuthor({
      foreignAuthorId,
      audiobookRootFolderPath: addAudiobooks ? audiobookRootFolderPath?.value : null,
      ebookRootFolderPath: addEbooks ? ebookRootFolderPath?.value : null,
      mediaType: selectedMediaType,
      monitor: selectedMonitor,
      audiobookMonitorExistingMode: addAudiobooks ? audiobookMonitorValue : null,
      ebookMonitorExistingMode: addEbooks ? ebookMonitorValue : null,
      monitorNewItems: selectedMonitorNewItems,
      audiobookMonitored: addAudiobooks ? audiobookMonitored?.value !== false : null,
      ebookMonitored: addEbooks ? ebookMonitored?.value !== false : null,
      // Provide per-type monitor values so the thunk can submit both requests correctly
      audiobookMonitor: addAudiobooks ? audiobookMonitorValue : null,
      ebookMonitor: addEbooks ? ebookMonitorValue : null,
      audiobookMonitorNewItems: addAudiobooks ? audiobookMonitorNewItemsValue : null,
      ebookMonitorNewItems: addEbooks ? ebookMonitorNewItemsValue : null,
      audiobookQualityProfileId: addAudiobooks && audiobookProfile !== 'none' ? audiobookProfile : null,
      ebookQualityProfileId: addEbooks && ebookProfile !== 'none' ? ebookProfile : null,
      // Pass per-type metadata profile IDs so the thunk can choose correctly
      audiobookMetadataProfileId: addAudiobooks ? audiobookMetadataProfileId?.value : null,
      ebookMetadataProfileId: addEbooks ? ebookMetadataProfileId?.value : null,
      metadataProfileId: metadataProfileId.value,
      tags: tags.value,
      searchForMissingBooks
    });
  };

  //
  // Render

  render() {
    return (
      <AddNewAuthorModalContent
        {...this.props}
        selectedMediaType={this.state.selectedMediaType}
        onInputChange={this.onInputChange}
        onAddAuthorPress={this.onAddAuthorPress}
        onMediaTypeChange={this.onMediaTypeChange}
        onSetDefaultMediaType={this.onSetDefaultMediaType}
      />
    );
  }
}

AddNewAuthorModalContentConnector.propTypes = {
  foreignAuthorId: PropTypes.string.isRequired,
  defaultMediaType: PropTypes.string,
  isSavingDefaultMediaType: PropTypes.bool.isRequired,
  uiSaveError: PropTypes.object,
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  monitor: PropTypes.object.isRequired,
  audiobookMonitored: PropTypes.object,
  ebookMonitored: PropTypes.object,
  audiobookMonitor: PropTypes.object,
  ebookMonitor: PropTypes.object,
  monitorNewItems: PropTypes.object.isRequired,
  audiobookMonitorNewItems: PropTypes.object,
  ebookMonitorNewItems: PropTypes.object,
  audiobookQualityProfileId: PropTypes.object,
  ebookQualityProfileId: PropTypes.object,
  audiobookMetadataProfileId: PropTypes.object,
  ebookMetadataProfileId: PropTypes.object,
  metadataProfileId: PropTypes.object,
  tags: PropTypes.object.isRequired,
  rootFoldersPopulated: PropTypes.bool.isRequired,
  isAdded: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  setAuthorAddDefault: PropTypes.func.isRequired,
  addAuthor: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  resetAddState: PropTypes.func.isRequired,
  setUISettingsValue: PropTypes.func.isRequired,
  saveUISettings: PropTypes.func.isRequired,
  showMessage: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AddNewAuthorModalContentConnector);
