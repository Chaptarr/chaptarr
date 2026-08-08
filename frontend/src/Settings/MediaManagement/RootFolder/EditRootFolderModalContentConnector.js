import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { coerceFolderType } from 'Helpers/Props/folderTypes';
import { deleteRootFolder, fetchMediaManagementSettings, fetchRootFolders, saveMediaManagementSettings, saveRootFolder, setMediaManagementSettingsValue, setRootFolderValue } from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import EditRootFolderModalContent from './EditRootFolderModalContent';
import {
  cleanRootFolderPath,
  getCompatibleRootFolders,
  getRootFoldersWithCandidate,
  rootFolderMediaTypes,
  rootFolderSupportsMediaType
} from './rootFolderDefaultUtils';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.rootFolders,
    createProviderSettingsSelector('rootFolders'),
    createSettingsSectionSelector('mediaManagement'),
    (state, { folderType }) => folderType,
    (advancedSettings, metadataProfiles, rootFolders, rootFolderSettings, mediaManagementSettings, folderTypeProp) => {
      const mediaManagement = mediaManagementSettings.settings || {};

      return {
        ...rootFolderSettings,
        advancedSettings,
        showMetadataProfile: metadataProfiles.items.length > 1,
        rootFolders: rootFolders.items || [],
        isMediaManagementPopulated: mediaManagementSettings.isPopulated,
        defaultAudiobookRootFolderPath: mediaManagement.defaultAudiobookRootFolderPath?.value || '',
        defaultEbookRootFolderPath: mediaManagement.defaultEbookRootFolderPath?.value || '',
        folderTypeProp
      };
    }
  );
}

const mapDispatchToProps = {
  setRootFolderValue,
  saveRootFolder,
  deleteRootFolder,
  fetchMediaManagementSettings,
  setMediaManagementSettingsValue,
  saveMediaManagementSettings,
  fetchRootFolders
};

class EditRootFolderModalContentConnector extends Component {
  state = {
    defaultAudiobookRootFolder: null,
    defaultEbookRootFolder: null
  };

  componentDidMount() {
    const {
      id,
      folderTypeProp,
      isMediaManagementPopulated,
      fetchMediaManagementSettings: fetchMediaManagementSettingsAction,
      setRootFolderValue: setRootFolderValueAction
    } = this.props;

    if (!isMediaManagementPopulated) {
      fetchMediaManagementSettingsAction();
    }

    if (!id && folderTypeProp != null) {
      setRootFolderValueAction({ name: 'folderType', value: coerceFolderType(folderTypeProp) });
    }
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.saveDefaultRootFolderSettings();

      if (this.pendingNewRootFolderPath && this.props.onRootFolderAdded) {
        this.props.onRootFolderAdded({ value: this.pendingNewRootFolderPath });
      }

      this.clearPendingRootFolderSave();

      this.setState({
        defaultAudiobookRootFolder: null,
        defaultEbookRootFolder: null
      });

      if (this.props.onSaveSuccess) {
        this.props.onSaveSuccess();
      }

      this.onModalClose();
    }
  }

  pendingNewRootFolderPath = null;
  pendingOriginalRootFolderPath = null;
  pendingRootFolder = null;
  pendingDefaultRootFolderStates = null;

  getExistingRootFolder = () => {
    const { id, item, rootFolders } = this.props;
    const rootFolderId = item?.id?.value || id;

    return rootFolders.find((rootFolder) => rootFolder.id === rootFolderId);
  };

  getRootFolderCandidate = () => {
    const { id, item, folderTypeProp } = this.props;

    return {
      id: item?.id?.value || id,
      path: cleanRootFolderPath(item?.path?.value),
      folderType: coerceFolderType(item?.folderType?.value ?? folderTypeProp)
    };
  };

  getOriginalRootFolderPath = () => {
    const { item } = this.props;
    const existingRootFolder = this.getExistingRootFolder();

    return cleanRootFolderPath(
      item?.path?.previousValue ||
      existingRootFolder?.path ||
      item?.path?.value
    );
  };

  getDefaultRootFolderPath = (mediaType) => {
    return mediaType === rootFolderMediaTypes.AUDIOBOOK ?
      this.props.defaultAudiobookRootFolderPath :
      this.props.defaultEbookRootFolderPath;
  };

  getDefaultRootFolderOverride = (mediaType) => {
    return mediaType === rootFolderMediaTypes.AUDIOBOOK ?
      this.state.defaultAudiobookRootFolder :
      this.state.defaultEbookRootFolder;
  };

  getDefaultRootFolderState = (mediaType) => {
    const candidateRootFolder = this.getRootFolderCandidate();
    const rootFolders = getRootFoldersWithCandidate(this.props.rootFolders, candidateRootFolder);
    const compatibleRootFolders = getCompatibleRootFolders(rootFolders, mediaType);
    const candidatePath = cleanRootFolderPath(candidateRootFolder.path);
    const defaultRootFolderPath = cleanRootFolderPath(this.getDefaultRootFolderPath(mediaType));
    const override = this.getDefaultRootFolderOverride(mediaType);
    const hasOverride = override != null;
    const supportsMediaType = !!candidatePath && rootFolderSupportsMediaType(candidateRootFolder, mediaType);
    // Mirrors RootFolderDefaultResolver for unsaved root folders that cannot have API flags yet.
    const isOnlyCompatibleRootFolder = supportsMediaType &&
      compatibleRootFolders.length === 1 &&
      cleanRootFolderPath(compatibleRootFolders[0].path) === candidatePath;
    const existingRootFolder = this.getExistingRootFolder();
    const matchesExistingRootFolder = cleanRootFolderPath(existingRootFolder?.path) === candidatePath;
    const isSavedEffectiveDefault = matchesExistingRootFolder && !defaultRootFolderPath && (
      mediaType === rootFolderMediaTypes.AUDIOBOOK ?
        existingRootFolder?.isEffectiveDefaultAudiobook :
        existingRootFolder?.isEffectiveDefaultEbook
    );
    const isEffectiveDefault = isOnlyCompatibleRootFolder ||
      (!!defaultRootFolderPath && defaultRootFolderPath === candidatePath) ||
      isSavedEffectiveDefault;

    return {
      value: supportsMediaType && (isOnlyCompatibleRootFolder || (hasOverride ? override : isEffectiveDefault)),
      hasOverride,
      isAutomatic: !defaultRootFolderPath && isOnlyCompatibleRootFolder,
      isDisabled: !this.props.isMediaManagementPopulated || !supportsMediaType || isOnlyCompatibleRootFolder
    };
  };

  getDefaultRootFolderSettingUpdates = () => {
    const rootFolder = this.pendingRootFolder;

    if (!rootFolder?.path) {
      return {};
    }

    const originalRootFolderPath = this.pendingOriginalRootFolderPath;
    const defaultStates = this.pendingDefaultRootFolderStates || {};
    const updates = {};

    const addUpdate = (mediaType, settingName) => {
      const defaultState = defaultStates[mediaType] || this.getDefaultRootFolderState(mediaType);
      const currentDefaultRootFolderPath = cleanRootFolderPath(this.getDefaultRootFolderPath(mediaType));
      const supportsMediaType = rootFolderSupportsMediaType(rootFolder, mediaType);
      const matchesCurrentPath = !!currentDefaultRootFolderPath &&
        currentDefaultRootFolderPath === rootFolder.path;
      const matchesOriginalPath = !!currentDefaultRootFolderPath &&
        currentDefaultRootFolderPath === originalRootFolderPath;

      if (!supportsMediaType) {
        if (matchesCurrentPath || matchesOriginalPath) {
          updates[settingName] = '';
        }

        return;
      }

      if (matchesOriginalPath && currentDefaultRootFolderPath !== rootFolder.path) {
        updates[settingName] = rootFolder.path;
        return;
      }

      if (!defaultState.hasOverride) {
        return;
      }

      if (defaultState.value) {
        if (currentDefaultRootFolderPath !== rootFolder.path) {
          updates[settingName] = rootFolder.path;
        }
      } else if (matchesCurrentPath || matchesOriginalPath) {
        updates[settingName] = '';
      }
    };

    addUpdate(rootFolderMediaTypes.AUDIOBOOK, 'defaultAudiobookRootFolderPath');
    addUpdate(rootFolderMediaTypes.EBOOK, 'defaultEbookRootFolderPath');

    return updates;
  };

  saveDefaultRootFolderSettings = () => {
    if (!this.props.isMediaManagementPopulated) {
      return;
    }

    const updates = this.getDefaultRootFolderSettingUpdates();
    const updateNames = Object.keys(updates);

    if (!updateNames.length) {
      return;
    }

    updateNames.forEach((name) => {
      this.props.setMediaManagementSettingsValue({ name, value: updates[name] });
    });

    const saveRequest = this.props.saveMediaManagementSettings(updates);

    if (saveRequest?.done) {
      saveRequest.done(() => {
        this.props.fetchRootFolders();
      });
    }
  };

  clearPendingRootFolderSave = () => {
    this.pendingNewRootFolderPath = null;
    this.pendingOriginalRootFolderPath = null;
    this.pendingRootFolder = null;
    this.pendingDefaultRootFolderStates = null;
  };

  onInputChange = ({ name, value }) => {
    this.props.setRootFolderValue({ name, value });
  };

  onDefaultAudiobookRootFolderChange = ({ value }) => {
    this.setState({ defaultAudiobookRootFolder: value });
  };

  onDefaultEbookRootFolderChange = ({ value }) => {
    this.setState({ defaultEbookRootFolder: value });
  };

  onSavePress = () => {
    const { id, item } = this.props;
    this.pendingNewRootFolderPath = null;
    this.pendingOriginalRootFolderPath = this.getOriginalRootFolderPath();
    this.pendingRootFolder = this.getRootFolderCandidate();
    this.pendingDefaultRootFolderStates = {
      [rootFolderMediaTypes.AUDIOBOOK]: this.getDefaultRootFolderState(rootFolderMediaTypes.AUDIOBOOK),
      [rootFolderMediaTypes.EBOOK]: this.getDefaultRootFolderState(rootFolderMediaTypes.EBOOK)
    };

    if (!id && this.props.onRootFolderAdded) {
      const path = item?.path?.value;

      if (path) {
        this.pendingNewRootFolderPath = cleanRootFolderPath(path);
      }
    }

    this.props.saveRootFolder({ id: this.props.id });
  };

  onDeleteRootFolderPress = () => {
    this.clearPendingRootFolderSave();

    if (this.props.onDeleteRootFolderPress) {
      this.props.onDeleteRootFolderPress();
      return;
    }

    this.props.deleteRootFolder({ id: this.props.id });
    this.onModalClose();
  };

  onModalClose = () => {
    this.clearPendingRootFolderSave();
    this.props.onModalClose();
  };

  render() {
    const audiobookDefaultRootFolderState = this.getDefaultRootFolderState(rootFolderMediaTypes.AUDIOBOOK);
    const ebookDefaultRootFolderState = this.getDefaultRootFolderState(rootFolderMediaTypes.EBOOK);

    return (
      <EditRootFolderModalContent
        {...this.props}
        isDefaultAudiobookRootFolder={audiobookDefaultRootFolderState.value}
        isDefaultAudiobookRootFolderDisabled={audiobookDefaultRootFolderState.isDisabled}
        isDefaultAudiobookRootFolderAutomatic={audiobookDefaultRootFolderState.isAutomatic}
        isDefaultEbookRootFolder={ebookDefaultRootFolderState.value}
        isDefaultEbookRootFolderDisabled={ebookDefaultRootFolderState.isDisabled}
        isDefaultEbookRootFolderAutomatic={ebookDefaultRootFolderState.isAutomatic}
        onSavePress={this.onSavePress}
        onInputChange={this.onInputChange}
        onDefaultAudiobookRootFolderChange={this.onDefaultAudiobookRootFolderChange}
        onDefaultEbookRootFolderChange={this.onDefaultEbookRootFolderChange}
        onDeleteRootFolderPress={this.onDeleteRootFolderPress}
        onModalClose={this.onModalClose}
      />
    );
  }
}

EditRootFolderModalContentConnector.propTypes = {
  id: PropTypes.number,
  folderTypeProp: PropTypes.number,
  isFetching: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  isMediaManagementPopulated: PropTypes.bool.isRequired,
  defaultAudiobookRootFolderPath: PropTypes.string.isRequired,
  defaultEbookRootFolderPath: PropTypes.string.isRequired,
  onRootFolderAdded: PropTypes.func,
  setRootFolderValue: PropTypes.func.isRequired,
  saveRootFolder: PropTypes.func.isRequired,
  deleteRootFolder: PropTypes.func.isRequired,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteRootFolderPress: PropTypes.func,
  onSaveSuccess: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditRootFolderModalContentConnector);
