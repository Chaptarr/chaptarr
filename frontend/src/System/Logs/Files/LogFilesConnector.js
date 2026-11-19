import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchGeneralSettings, saveGeneralSettingsFields } from 'Store/Actions/settingsActions';
import { fetchLogFiles } from 'Store/Actions/systemActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import combinePath from 'Utilities/String/combinePath';
import LogFiles from './LogFiles';

function createMapStateToProps() {
  return createSelector(
    (state) => state.system.logFiles,
    (state) => state.system.status.item,
    (state) => state.settings.general,
    createCommandExecutingSelector(commandNames.DELETE_LOG_FILES),
    (logFiles, status, generalSettings, deleteFilesExecuting) => {
      const {
        isFetching,
        items
      } = logFiles;

      const {
        appData,
        isWindows
      } = status;

      const logLevel = generalSettings.item.logLevel || 'info';

      return {
        isFetching,
        items,
        deleteFilesExecuting,
        currentLogView: 'Log Files',
        location: combinePath(isWindows, appData, ['logs']),
        logLevel,
        isSettingsLoaded: generalSettings.isPopulated && !generalSettings.isFetching && !generalSettings.error,
        settingsError: generalSettings.error
      };
    }
  );
}

const mapDispatchToProps = {
  fetchLogFiles,
  executeCommand,
  fetchGeneralSettings,
  saveGeneralSettings: saveGeneralSettingsFields
};

class LogFilesConnector extends Component {
  state = { isSavingSettings: false, saveSettingsError: null };

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchLogFiles();
    this.props.fetchGeneralSettings();
  }

  componentWillUnmount() {
    this.saveRequest = null;
  }

  //
  // Listeners

  onRefreshPress = () => {
    this.props.fetchLogFiles();
    if (!this.props.isSettingsLoaded) {
      this.props.fetchGeneralSettings();
    }
  };

  onDeleteFilesPress = () => {
    this.props.executeCommand({
      name: commandNames.DELETE_LOG_FILES,
      commandFinished: this.onCommandFinished
    });
  };

  onCommandFinished = () => {
    this.props.fetchLogFiles();
  };

  onLogLevelChange = ({ value }) => {
    if (!this.props.isSettingsLoaded || this.state.isSavingSettings) {
      return;
    }
    this.setState({ isSavingSettings: true, saveSettingsError: null });
    const request = this.props.saveGeneralSettings({ logLevel: value });
    this.saveRequest = request;
    request.done(() => {
      if (this.saveRequest === request) {
        this.setState({ isSavingSettings: false });
      }
    });
    request.fail((saveSettingsError) => {
      if (this.saveRequest === request) {
        this.setState({ isSavingSettings: false, saveSettingsError });
      }
    });
  };

  //
  // Render

  render() {
    return (
      <LogFiles
        onRefreshPress={this.onRefreshPress}
        onDeleteFilesPress={this.onDeleteFilesPress}
        onLogLevelChange={this.onLogLevelChange}
        {...this.props}
        {...this.state}
      />
    );
  }
}

LogFilesConnector.propTypes = {
  isSettingsLoaded: PropTypes.bool.isRequired,
  deleteFilesExecuting: PropTypes.bool.isRequired,
  fetchLogFiles: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  fetchGeneralSettings: PropTypes.func.isRequired,
  saveGeneralSettings: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(LogFilesConnector);
