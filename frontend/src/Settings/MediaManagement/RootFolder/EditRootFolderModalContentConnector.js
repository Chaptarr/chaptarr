import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { coerceFolderType } from 'Helpers/Props/folderTypes';
import { deleteRootFolder, fetchMediaManagementSettings, fetchRootFolders, saveMediaManagementSettingsFields, saveRootFolder, setRootFolderValue } from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import translate from 'Utilities/String/translate';
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
    (state) => state.settings.mediaManagement,
    (state, { folderType }) => folderType,
    (advancedSettings, metadataProfiles, rootFolders, rootFolderSettings, mediaManagementSettings, folderTypeProp) => {
      const mediaManagement = mediaManagementSettings.item || {};

      return {
        ...rootFolderSettings,
        advancedSettings,
        showMetadataProfile: metadataProfiles.items.length > 1,
        rootFolders: rootFolders.items || [],
        rootFolderSection: rootFolders,
        isMediaManagementPopulated: mediaManagementSettings.isPopulated,
        isMediaManagementFetching: mediaManagementSettings.isFetching,
        mediaManagementError: mediaManagementSettings.error,
        defaultAudiobookRootFolderPath: mediaManagement.defaultAudiobookRootFolderPath || '',
        defaultEbookRootFolderPath: mediaManagement.defaultEbookRootFolderPath || '',
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
  saveMediaManagementSettings: saveMediaManagementSettingsFields,
  fetchRootFolders
};

class EditRootFolderModalContentConnector extends Component {
  state = {
    defaultAudiobookRootFolder: null,
    defaultEbookRootFolder: null,
    savedRootFolderId: null,
    isSaving: false,
    saveError: null
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

  componentWillUnmount() {
    this.saveRequest = null;
  }

  pendingNewRootFolderPath = null;
  originalRootFolderPath = null;
  savedRootFolderSelector = createProviderSettingsSelector('rootFolders');

  getItem = () => {
    if (!this.state.savedRootFolderId) {
      return this.props.item;
    }

    return this.savedRootFolderSelector({ settings: {
      rootFolders: { ...this.props.rootFolderSection, saveError: this.state.saveError }
    } }, { id: this.state.savedRootFolderId }).item;
  };

  getExistingRootFolder = () => {
    const { rootFolders } = this.props;
    const item = this.getItem();
    const id = this.state.savedRootFolderId || this.props.id;
    const rootFolderId = item?.id?.value || id;

    return rootFolders.find((rootFolder) => rootFolder.id === rootFolderId);
  };

  getRootFolderCandidate = () => {
    const { folderTypeProp } = this.props;
    const item = this.getItem();
    const id = this.state.savedRootFolderId || this.props.id;

    return {
      id: item?.id?.value || id,
      path: cleanRootFolderPath(item?.path?.value),
      folderType: coerceFolderType(item?.folderType?.value ?? folderTypeProp)
    };
  };

  getOriginalRootFolderPath = () => {
    const item = this.getItem();
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
      (!!defaultRootFolderPath && (defaultRootFolderPath === candidatePath ||
        defaultRootFolderPath === (this.originalRootFolderPath ?? this.getOriginalRootFolderPath()))) ||
      isSavedEffectiveDefault;

    return {
      value: supportsMediaType && (isOnlyCompatibleRootFolder || (hasOverride ? override : isEffectiveDefault)),
      hasOverride,
      isAutomatic: !defaultRootFolderPath && isOnlyCompatibleRootFolder,
      isDisabled: !this.props.isMediaManagementPopulated || !supportsMediaType || isOnlyCompatibleRootFolder
    };
  };

  getDefaultRootFolderSettingUpdates = (rootFolder, originalRootFolderPath, defaultStates) => {
    if (!rootFolder?.path) {
      return {};
    }

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

      if (!defaultState.hasOverride && matchesOriginalPath && currentDefaultRootFolderPath !== rootFolder.path) {
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
    if (this.state.isSaving || !this.props.isMediaManagementPopulated || this.props.isMediaManagementFetching || this.props.mediaManagementError) {
      return;
    }
    const rootFolder = this.getRootFolderCandidate();
    this.originalRootFolderPath ??= this.getOriginalRootFolderPath();
    const updates = this.getDefaultRootFolderSettingUpdates(
      rootFolder,
      this.originalRootFolderPath,
      {
        [rootFolderMediaTypes.AUDIOBOOK]: this.getDefaultRootFolderState(rootFolderMediaTypes.AUDIOBOOK),
        [rootFolderMediaTypes.EBOOK]: this.getDefaultRootFolderState(rootFolderMediaTypes.EBOOK)
      }
    );

    if (!rootFolder.id || this.pendingNewRootFolderPath) {
      this.pendingNewRootFolderPath = rootFolder.path;
    }

    this.setState({ isSaving: true, saveError: null });
    let rootSaved = false;
    const request = this.props.saveRootFolder({ id: rootFolder.id }).then((savedRootFolder) => {
      rootSaved = true;
      if (this.saveRequest === request) {
        this.setState({ savedRootFolderId: savedRootFolder.id });
      }

      if (Object.keys(updates).length) {
        return this.props.saveMediaManagementSettings(updates).done(() => this.props.fetchRootFolders());
      }
    });
    this.saveRequest = request;

    request.done(() => {
      if (this.saveRequest !== request) {
        return;
      }

      if (this.pendingNewRootFolderPath) {
        this.props.onRootFolderAdded?.({ value: this.pendingNewRootFolderPath });
      }
      this.originalRootFolderPath = null;
      this.props.onSaveSuccess?.();
      this.setState({ isSaving: false }, this.onModalClose);
    });
    request.fail((saveError) => {
      if (this.saveRequest === request) {
        this.setState({
          isSaving: false,
          saveError: rootSaved ? { message: translate('RootFolderSavedDefaultFailed') } : saveError
        });
      }
    });
  };

  onDeleteRootFolderPress = () => {
    if (this.props.onDeleteRootFolderPress) {
      this.props.onDeleteRootFolderPress();
      return;
    }

    this.props.deleteRootFolder({ id: this.state.savedRootFolderId || this.props.id });
    this.onModalClose();
  };

  onModalClose = () => {
    if (this.state.isSaving) {
      return;
    }
    this.saveRequest = null;
    this.pendingNewRootFolderPath = null;
    this.originalRootFolderPath = null;
    this.props.onModalClose();
  };

  render() {
    const audiobookDefaultRootFolderState = this.getDefaultRootFolderState(rootFolderMediaTypes.AUDIOBOOK);
    const ebookDefaultRootFolderState = this.getDefaultRootFolderState(rootFolderMediaTypes.EBOOK);

    return (
      <EditRootFolderModalContent
        {...this.props}
        id={this.state.savedRootFolderId || this.props.id}
        item={this.getItem()}
        isSaving={this.state.isSaving}
        saveError={this.state.saveError}
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
  item: PropTypes.object.isRequired,
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  rootFolderSection: PropTypes.object.isRequired,
  isMediaManagementPopulated: PropTypes.bool.isRequired,
  isMediaManagementFetching: PropTypes.bool.isRequired,
  mediaManagementError: PropTypes.object,
  defaultAudiobookRootFolderPath: PropTypes.string.isRequired,
  defaultEbookRootFolderPath: PropTypes.string.isRequired,
  onRootFolderAdded: PropTypes.func,
  setRootFolderValue: PropTypes.func.isRequired,
  saveRootFolder: PropTypes.func.isRequired,
  deleteRootFolder: PropTypes.func.isRequired,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteRootFolderPress: PropTypes.func,
  onSaveSuccess: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps, null, { forwardRef: true })(EditRootFolderModalContentConnector);
