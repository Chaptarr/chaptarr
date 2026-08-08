import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { addRecentFolder, removeRecentFolder, setInteractiveImportMode } from 'Store/Actions/interactiveImportActions';
import { fetchMediaManagementSettings, saveMediaManagementSettings, setMediaManagementSettingsValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import InteractiveImportSelectFolderModalContent from './InteractiveImportSelectFolderModalContent';

const LARGE_INTERACTIVE_IMPORT_FILE_COUNT = 100;

function createMapStateToProps() {
  return createSelector(
    (state) => state.interactiveImport.recentFolders,
    (state) => state.interactiveImport.importMode,
    createSettingsSectionSelector('mediaManagement'),
    (recentFolders, importMode, mediaManagementSettings) => {
      return {
        recentFolders,
        importMode,
        mediaManagementSettings
      };
    }
  );
}

const mapDispatchToProps = {
  addRecentFolder,
  removeRecentFolder,
  setInteractiveImportMode,
  executeCommand,
  fetchMediaManagementSettings,
  setMediaManagementSettingsValue,
  saveMediaManagementSettings
};

class InteractiveImportSelectFolderModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isCheckingInteractiveImportFolder: false,
      largeFolderWarning: null
    };
  }

  componentDidMount() {
    if (!this.props.mediaManagementSettings.isPopulated) {
      this.props.fetchMediaManagementSettings();
    }
  }

  //
  // Listeners

  onQuickImportPress = (folder, importMode) => {
    this.props.setInteractiveImportMode({ importMode });
    this.props.addRecentFolder({ folder });

    this.props.executeCommand({
      name: commandNames.DOWNLOADED_BOOKS_SCAN,
      path: folder,
      importMode,
      requireDefaultRootFolderForMissingAuthors: true
    });

    this.props.onModalClose();
  };

  onInteractiveImportPress = (folder, importMode) => {
    this.props.setInteractiveImportMode({ importMode });

    this.setState({
      isCheckingInteractiveImportFolder: true,
      largeFolderWarning: null
    });

    const { request } = createAjaxRequest({
      url: '/filesystem/mediafiles',
      data: { path: folder }
    });

    request.done((data) => {
      const fileCount = Array.isArray(data) ? data.length : 0;

      if (fileCount > LARGE_INTERACTIVE_IMPORT_FILE_COUNT) {
        this.setState({
          isCheckingInteractiveImportFolder: false,
          largeFolderWarning: {
            folder,
            fileCount
          }
        });

        return;
      }

      this.openInteractiveImport(folder);
    });

    request.fail(() => {
      this.openInteractiveImport(folder);
    });
  };

  onConfirmInteractiveImportPress = (folder, importMode) => {
    this.props.setInteractiveImportMode({ importMode });
    this.openInteractiveImport(folder);
  };

  onPathFallbackChange = ({ value }) => {
    const { mediaManagementSettings } = this.props;

    if (mediaManagementSettings.settings?.bookMatchingStrictness?.value === 'strict' && value) {
      return;
    }

    this.props.setMediaManagementSettingsValue({
      name: 'usePathAsTagsFallback',
      value
    });

    this.props.saveMediaManagementSettings({
      usePathAsTagsFallback: value
    });
  };

  onRemoveRecentFolderPress = (folder) => {
    this.props.removeRecentFolder({ folder });
  };

  openInteractiveImport = (folder) => {
    this.setState({
      isCheckingInteractiveImportFolder: false,
      largeFolderWarning: null
    });

    this.props.addRecentFolder({ folder });
    this.props.onFolderSelect(folder);
  };

  //
  // Render

  render() {
    if (this.path) {
      return null;
    }

    return (
      <InteractiveImportSelectFolderModalContent
        {...this.props}
        isCheckingInteractiveImportFolder={this.state.isCheckingInteractiveImportFolder}
        largeFolderWarning={this.state.largeFolderWarning}
        onQuickImportPress={this.onQuickImportPress}
        onInteractiveImportPress={this.onInteractiveImportPress}
        onConfirmInteractiveImportPress={this.onConfirmInteractiveImportPress}
        onPathFallbackChange={this.onPathFallbackChange}
        onRemoveRecentFolderPress={this.onRemoveRecentFolderPress}
      />
    );
  }
}

InteractiveImportSelectFolderModalContentConnector.propTypes = {
  path: PropTypes.string,
  onFolderSelect: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  addRecentFolder: PropTypes.func.isRequired,
  removeRecentFolder: PropTypes.func.isRequired,
  setInteractiveImportMode: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  importMode: PropTypes.string,
  mediaManagementSettings: PropTypes.object.isRequired,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(InteractiveImportSelectFolderModalContentConnector);
