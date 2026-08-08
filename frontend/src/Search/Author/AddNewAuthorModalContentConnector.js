import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { addAuthor, resetAddState, setAuthorAddDefault } from 'Store/Actions/searchActions';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import { showMessage } from 'Store/Actions/appActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createRootFolderDefaultsSelector from 'Store/Selectors/createRootFolderDefaultsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import AddNewAuthorModalContent from './AddNewAuthorModalContent';

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

        const monitorExistingToKey = (monitorExisting) => {
          // 0=None, 1=All, 2=Selected
          if (monitorExisting === 1) {
            return 'all';
          }

          if (monitorExisting === 2) {
            return 'select';
          }

          return 'none';
        };

        const monitorFutureToKey = (monitorFuture) => {
          return monitorFuture ? 'all' : 'none';
        };

        const audiobookRoot = settings.audiobookRootFolderPath?.value;
        const ebookRoot = settings.ebookRootFolderPath?.value;

        const audiobookRootFolder = rootFolders.find((f) => f.path === audiobookRoot);
        const ebookRootFolder = rootFolders.find((f) => f.path === ebookRoot);

        // Inherit defaults from the selected root folder (per media type). Users can override by changing the fields.
        if (!settings.audiobookMonitor && audiobookRootFolder) {
          settings.audiobookMonitor = {
            ...(settings.audiobookMonitor || {}),
            value: monitorExistingToKey(audiobookRootFolder.audiobookMonitorExisting)
          };
        }

        if (!settings.audiobookMonitorNewItems && audiobookRootFolder) {
          settings.audiobookMonitorNewItems = {
            ...(settings.audiobookMonitorNewItems || {}),
            value: monitorFutureToKey(audiobookRootFolder.audiobookMonitorFuture)
          };
        }

        if (!settings.ebookMonitor && ebookRootFolder) {
          settings.ebookMonitor = {
            ...(settings.ebookMonitor || {}),
            value: monitorExistingToKey(ebookRootFolder.ebookMonitorExisting)
          };
        }

        if (!settings.ebookMonitorNewItems && ebookRootFolder) {
          settings.ebookMonitorNewItems = {
            ...(settings.ebookMonitorNewItems || {}),
            value: monitorFutureToKey(ebookRootFolder.ebookMonitorFuture)
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

    // Validate that at least one quality profile is selected
    const audiobookProfile = audiobookQualityProfileId?.value;
    const ebookProfile = ebookQualityProfileId?.value;

    if ((!audiobookProfile || audiobookProfile === 'none') &&
        (!ebookProfile || ebookProfile === 'none')) {
      // This should be handled by validation, but as a safety check
      console.error('At least one quality profile must be selected');
      return;
    }

    const { selectedMediaType } = this.state;

    // The UI stores media-type-specific monitor values under audiobookMonitor/ebookMonitor.
    // Fall back to the legacy monitor/monitorNewItems fields when the specific ones haven't been touched.
    const audiobookMonitorValue = audiobookMonitor?.value || monitor.value;
    const ebookMonitorValue = ebookMonitor?.value || monitor.value;

    const audiobookMonitorNewItemsValue = audiobookMonitorNewItems?.value || monitorNewItems.value;
    const ebookMonitorNewItemsValue = ebookMonitorNewItems?.value || monitorNewItems.value;

    let selectedMonitor = audiobookMonitorValue;
    let selectedMonitorNewItems = audiobookMonitorNewItemsValue;

    if (selectedMediaType === 'ebook') {
      selectedMonitor = ebookMonitorValue;
      selectedMonitorNewItems = ebookMonitorNewItemsValue;
    }

    this.props.addAuthor({
      foreignAuthorId,
      audiobookRootFolderPath: audiobookRootFolderPath?.value,
      ebookRootFolderPath: ebookRootFolderPath?.value,
      mediaType: selectedMediaType,
      monitor: selectedMonitor,
      monitorNewItems: selectedMonitorNewItems,
      // Provide per-type monitor values so the thunk can submit both requests correctly
      audiobookMonitor: audiobookMonitorValue,
      ebookMonitor: ebookMonitorValue,
      audiobookMonitorNewItems: audiobookMonitorNewItemsValue,
      ebookMonitorNewItems: ebookMonitorNewItemsValue,
      audiobookQualityProfileId: audiobookProfile === 'none' ? null : audiobookProfile,
      ebookQualityProfileId: ebookProfile === 'none' ? null : ebookProfile,
      // Pass per-type metadata profile IDs so the thunk can choose correctly
      audiobookMetadataProfileId: audiobookMetadataProfileId?.value,
      ebookMetadataProfileId: ebookMetadataProfileId?.value,
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
