import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchDownloadClients } from 'Store/Actions/Settings/downloadClients';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { fetchMediaManagementSettings, saveMediaManagementSettings, saveNamingSettings, setMediaManagementSettingsValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import MediaManagement from './MediaManagement';

const SECTION = 'mediaManagement';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    (state) => state.settings.naming,
    createSettingsSectionSelector(SECTION),
    createSystemStatusSelector(),
    (advancedSettings, namingSettings, sectionSettings, systemStatus) => {
      return {
        advancedSettings,
        ...sectionSettings,
        hasPendingChanges: !_.isEmpty(namingSettings.pendingChanges) || sectionSettings.hasPendingChanges,
        isWindows: systemStatus.isWindows
      };
    }
  );
}

const mapDispatchToProps = {
  fetchMediaManagementSettings,
  setMediaManagementSettingsValue,
  saveMediaManagementSettings,
  saveNamingSettings,
  clearPendingChanges,
  fetchDownloadClients,
  fetchRootFolders
};

class MediaManagementConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchMediaManagementSettings();
    this.props.fetchDownloadClients();
    this.props.fetchRootFolders();
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: `settings.${SECTION}` });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    if (name === 'bookMatchingStrictness') {
      this.props.setMediaManagementSettingsValue({ name, value });

      if (value === 'strict') {
        this.props.setMediaManagementSettingsValue({ name: 'usePathAsTagsFallback', value: false });
      }

      return;
    }

    if (name === 'usePathAsTagsFallback' &&
        this.props.settings?.bookMatchingStrictness?.value === 'strict' &&
        value) {
      return;
    }

    this.props.setMediaManagementSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.saveMediaManagementSettings();
    this.props.saveNamingSettings();
  };

  //
  // Render

  render() {
    return (
      <MediaManagement
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        {...this.props}
      />
    );
  }
}

MediaManagementConnector.propTypes = {
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  saveNamingSettings: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired,
  fetchDownloadClients: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  settings: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(MediaManagementConnector);
