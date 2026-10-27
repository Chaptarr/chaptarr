import PropTypes from 'prop-types';
import React, { Component, useState } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { addAuthor, resetAddState, setAuthorAddDefault } from 'Store/Actions/searchActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createExistingAuthorSelector from 'Store/Selectors/createExistingAuthorSelector';
import createRootFolderDefaultsSelector, { applyRootFolderMediaDefaults, getRootDerivedDefaultsForMediaType, rootDerivedAuthorDefaults } from 'Store/Selectors/createRootFolderDefaultsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import getAddAuthorSyncOptions from 'Utilities/Author/getAddAuthorSyncOptions';
import getLinkedAuthorMonitoringChanges from 'Utilities/Author/getLinkedAuthorMonitoringChanges';
import { resolveMonitorNewItemsOptionValue } from 'Utilities/Author/monitorNewItemsOptions';
import { resolveMonitorOptionValue } from 'Utilities/Author/monitorOptions';
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
    createExistingAuthorSelector(),
    (state, ownProps) => ownProps.draft,
    (searchState, metadataProfiles, qualityProfiles, rootFoldersState, uiState, rootFolderDefaults, dimensions, systemStatus, existingAuthor, draft) => {
      const {
        isAdding,
        isAdded,
        isQueued,
        addNotice,
        addError,
        authorDefaults
      } = searchState;

      const defaults = { ...authorDefaults };
      if (existingAuthor) {
        Object.assign(defaults, rootDerivedAuthorDefaults);
        for (const mediaType of ['audiobook', 'ebook']) {
          defaults[`${mediaType}RootFolderPath`] = existingAuthor[`${mediaType}RootFolderPath`] || '';
          if (existingAuthor[`${mediaType}RootFolderPath`]) {
            for (const field of ['Monitored', 'MonitorNewItems', 'QualityProfileId', 'MetadataProfileId', 'Tags']) {
              defaults[`${mediaType}${field}`] = existingAuthor[`${mediaType}${field}`] ?? defaults[`${mediaType}${field}`];
            }
          }
        }
      }

      const {
        settings,
        validationErrors,
        validationWarnings
      } = selectSettings(defaults, draft, addError);

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
      }

      applyRootFolderMediaDefaults(settings, rootFoldersState.isPopulated ? rootFoldersState.items : []);

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
        rootFolders: rootFoldersState.items,
        existingAuthor,
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

class AddNewAuthorModalContentController extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      selectedMediaType: normalizeDefaultMediaType(props.initialMediaType, true) ?? normalizeDefaultMediaType(props.defaultMediaType, true) ?? 'audiobook',
      syncOverrides: {}
    };

    this._didUserSelectMediaType = !!normalizeDefaultMediaType(props.initialMediaType, true);
    this._pendingDefaultSave = false;
  }

  //
  // Lifecycle

  componentDidMount() {
    // Root folders are the default source for each new author. Clear any
    // browser-persisted per-author overrides when this modal opens; choices
    // made after this point apply only to this add.
    this.props.resetAddState();
    if (!this.props.existingAuthor) {
      this.props.setAuthorAddDefault(rootDerivedAuthorDefaults);
    }

    // Ensure root folders are loaded
    if (!this.props.rootFoldersPopulated) {
      this.props.fetchRootFolders();
    }
  }

  componentDidUpdate(prevProps) {
    // Close modal when author is successfully added
    if (!prevProps.isAdded && this.props.isAdded) {
      this.props.onModalClose(true);
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

  getSyncOptions = () => getAddAuthorSyncOptions(
    this.props,
    this.props.rootFolders,
    this.state.selectedMediaType,
    this.props.existingAuthor,
    this.state.syncOverrides
  );

  setAuthorOptions = (changes) => {
    if (this.props.existingAuthor) {
      this.props.onDraftChange(changes);
    } else {
      this.props.setAuthorAddDefault(changes);
    }
  };

  onInputChange = ({ name, value }) => {
    if (name === 'syncMonitoredAcrossFormats' || name === 'sharedMonitor' || name === 'sharedMonitorNewItems') {
      this.setState(({ syncOverrides }) => ({ syncOverrides: name === 'syncMonitoredAcrossFormats' && value === false ?
        { syncMonitoredAcrossFormats: false } : { ...syncOverrides, [name]: value } }));
      if (name === 'sharedMonitor' || name === 'sharedMonitorNewItems') {
        this.setAuthorOptions(getLinkedAuthorMonitoringChanges(name, value, true));
      }
      return;
    }

    if (name === 'audiobookRootFolderPath' || name === 'ebookRootFolderPath') {
      this.setState(({ syncOverrides }) => ({ syncOverrides: { syncMonitoredAcrossFormats: syncOverrides.syncMonitoredAcrossFormats } }));
      const mediaType = name === 'audiobookRootFolderPath' ? 'audiobook' : 'ebook';
      this.setAuthorOptions({ ...getRootDerivedDefaultsForMediaType(mediaType), [name]: value });
      return;
    }

    this.setAuthorOptions({ [name]: value });
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
      authorName,
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
      tags,
      audiobookTags,
      ebookTags
    } = this.props;

    const { selectedMediaType } = this.state;
    const syncOptions = this.getSyncOptions();
    if (syncOptions.hasUnresolvedSyncOptions) {
      return;
    }
    const syncEnabled = syncOptions.syncMonitoredAcrossFormats.value === true;
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
    const audiobookMonitorValue = syncEnabled ? syncOptions.sharedMonitor.value : resolveMonitorOptionValue(audiobookMonitor?.value, monitor?.value);
    const ebookMonitorValue = syncEnabled ? syncOptions.sharedMonitor.value : resolveMonitorOptionValue(ebookMonitor?.value, monitor?.value);

    const audiobookMonitorNewItemsValue = syncEnabled ? syncOptions.sharedMonitorNewItems.value : resolveMonitorNewItemsOptionValue(audiobookMonitorNewItems?.value, monitorNewItems?.value);
    const ebookMonitorNewItemsValue = syncEnabled ? syncOptions.sharedMonitorNewItems.value : resolveMonitorNewItemsOptionValue(ebookMonitorNewItems?.value, monitorNewItems?.value);

    let selectedMonitor = audiobookMonitorValue;
    let selectedMonitorNewItems = audiobookMonitorNewItemsValue;
    let selectedTags = audiobookTags?.value;

    if (selectedMediaType === 'ebook') {
      selectedMonitor = ebookMonitorValue;
      selectedMonitorNewItems = ebookMonitorNewItemsValue;
      selectedTags = ebookTags?.value;
    } else if (selectedMediaType === 'both') {
      selectedTags = tags.value;
    }

    let syncChoice = undefined;
    if (syncOptions.canSyncMonitoredAcrossFormats) {
      syncChoice = syncEnabled;
    } else if (this.props.existingAuthor?.syncMonitoredAcrossFormats === false) {
      syncChoice = false;
    }

    this.props.addAuthor({
      foreignAuthorId,
      authorName,
      audiobookRootFolderPath: addAudiobooks ? audiobookRootFolderPath?.value : null,
      ebookRootFolderPath: addEbooks ? ebookRootFolderPath?.value : null,
      mediaType: selectedMediaType,
      syncMonitoredAcrossFormats: syncChoice,
      monitor: selectedMonitor,
      audiobookMonitorExistingMode: addAudiobooks ? audiobookMonitorValue : null,
      ebookMonitorExistingMode: addEbooks ? ebookMonitorValue : null,
      monitorNewItems: selectedMonitorNewItems,
      audiobookMonitored: addAudiobooks ? audiobookMonitored?.value !== false : null,
      ebookMonitored: addEbooks ? ebookMonitored?.value !== false : null,
      // Shared choices affect monitoring only; gates, profiles and tags remain independent.
      audiobookMonitor: addAudiobooks ? audiobookMonitorValue : null,
      ebookMonitor: addEbooks ? ebookMonitorValue : null,
      audiobookMonitorNewItems: addAudiobooks || syncEnabled ? audiobookMonitorNewItemsValue : null,
      ebookMonitorNewItems: addEbooks || syncEnabled ? ebookMonitorNewItemsValue : null,
      audiobookQualityProfileId: addAudiobooks && audiobookProfile !== 'none' ? audiobookProfile : null,
      ebookQualityProfileId: addEbooks && ebookProfile !== 'none' ? ebookProfile : null,
      // Pass per-type metadata profile IDs so the thunk can choose correctly
      audiobookMetadataProfileId: addAudiobooks ? audiobookMetadataProfileId?.value : null,
      ebookMetadataProfileId: addEbooks ? ebookMetadataProfileId?.value : null,
      metadataProfileId: metadataProfileId?.value,
      // Unselected previews never become configured media sides.
      tags: selectedTags,
      audiobookTags: addAudiobooks ? audiobookTags?.value : null,
      ebookTags: addEbooks ? ebookTags?.value : null,
      searchForMissingBooks
    });
  };

  //
  // Render

  render() {
    return (
      <AddNewAuthorModalContent
        {...this.props}
        {...this.getSyncOptions()}
        selectedMediaType={this.state.selectedMediaType}
        onInputChange={this.onInputChange}
        onAddAuthorPress={this.onAddAuthorPress}
        onMediaTypeChange={this.onMediaTypeChange}
        onSetDefaultMediaType={this.onSetDefaultMediaType}
      />
    );
  }
}

AddNewAuthorModalContentController.propTypes = {
  foreignAuthorId: PropTypes.string.isRequired,
  authorName: PropTypes.string.isRequired,
  defaultMediaType: PropTypes.string,
  initialMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']),
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
  audiobookTags: PropTypes.object,
  ebookTags: PropTypes.object,
  rootFoldersPopulated: PropTypes.bool.isRequired,
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  existingAuthor: PropTypes.object,
  isAdded: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  setAuthorAddDefault: PropTypes.func.isRequired,
  onDraftChange: PropTypes.func.isRequired,
  addAuthor: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  resetAddState: PropTypes.func.isRequired,
  setUISettingsValue: PropTypes.func.isRequired,
  saveUISettings: PropTypes.func.isRequired,
  showMessage: PropTypes.func.isRequired
};

const ConnectedAddNewAuthorModalContent = connect(createMapStateToProps, mapDispatchToProps)(AddNewAuthorModalContentController);

export default function AddNewAuthorModalContentConnector(props) {
  const [draft, setDraft] = useState({});

  return (
    <ConnectedAddNewAuthorModalContent
      {...props}
      draft={draft}
      onDraftChange={(changes) => setDraft((current) => ({ ...current, ...changes }))}
    />
  );
}
