import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchLogFiles } from 'Store/Actions/systemActions';
import { fetchGeneralSettings, saveGeneralSettings, setGeneralSettingsValue } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import combinePath from 'Utilities/String/combinePath';
import LogFiles from './LogFiles';

function createMapStateToProps() {
  return createSelector(
    (state) => state.system.logFiles,
    (state) => state.system.status.item,
    createSettingsSectionSelector('general'),
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

      const {
        settings,
        isSaving: isSavingSettings
      } = generalSettings;

      const logLevel = settings?.logLevel?.value || 'info';

      return {
        isFetching,
        items,
        deleteFilesExecuting,
        currentLogView: 'Log Files',
        location: combinePath(isWindows, appData, ['logs']),
        logLevel,
        isSavingSettings
      };
    }
  );
}

const mapDispatchToProps = {
  fetchLogFiles,
  executeCommand,
  fetchGeneralSettings,
  saveGeneralSettings,
  setGeneralSettingsValue
};

class LogFilesConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchLogFiles();
    this.props.fetchGeneralSettings();
  }

  //
  // Listeners

  onRefreshPress = () => {
    this.props.fetchLogFiles();
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
    this.props.setGeneralSettingsValue({ name: 'logLevel', value });
    
    // Save after a small delay to ensure state is updated
    setTimeout(() => {
      this.props.saveGeneralSettings();
    }, 100);
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
      />
    );
  }
}

LogFilesConnector.propTypes = {
  deleteFilesExecuting: PropTypes.bool.isRequired,
  fetchLogFiles: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  fetchGeneralSettings: PropTypes.func.isRequired,
  saveGeneralSettings: PropTypes.func.isRequired,
  setGeneralSettingsValue: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(LogFilesConnector);
