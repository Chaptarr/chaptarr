import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchDownloadClients } from 'Store/Actions/Settings/downloadClients';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { fetchMediaManagementSettings, invalidateBookCoverScopeResources, saveMediaManagementSettings, saveNamingSettings, setMediaManagementSettingsValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import createAjaxRequest from 'Utilities/createAjaxRequest';
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
  invalidateBookCoverScopeResources,
  clearPendingChanges,
  fetchDownloadClients,
  fetchRootFolders
};

class MediaManagementConnector extends Component {

  state = {
    isCoverDownloadScopeConfirmOpen: false,
    coverCleanupEstimate: null,
    isFetchingCoverCleanupEstimate: false,
    coverCleanupEstimateError: false
  };

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchMediaManagementSettings();
    this.props.fetchDownloadClients();
    this.props.fetchRootFolders();
  }

  componentWillUnmount() {
    this.cancelCoverCleanupEstimate();
    this.props.clearPendingChanges({ section: `settings.${SECTION}` });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setMediaManagementSettingsValue({ name, value });
  };

  onSavePress = () => {
    const coverDownloadScope = this.props.settings?.coverDownloadScope;

    if (coverDownloadScope?.pending &&
        coverDownloadScope.value === 'libraryBooks' &&
        coverDownloadScope.previousValue === 'allBooks') {
      this.setState({ isCoverDownloadScopeConfirmOpen: true });
      return;
    }

    this.saveSettings();
  };

  onConfirmCoverDownloadScope = () => {
    this.setState({ isCoverDownloadScopeConfirmOpen: false }, this.saveSettings);
  };

  onCancelCoverDownloadScope = () => {
    this.setState({ isCoverDownloadScopeConfirmOpen: false });
  };

  onCalculateCoverSavings = () => {
    this.cancelCoverCleanupEstimate();
    this.setState({ coverCleanupEstimate: null, isFetchingCoverCleanupEstimate: true, coverCleanupEstimateError: false });

    const pending = createAjaxRequest({ url: '/config/mediamanagement/covercleanup', dataType: 'json' });
    this.coverCleanupRequest = pending;

    pending.request.done((estimate) => {
      if (this.coverCleanupRequest === pending) {
        this.setState({ coverCleanupEstimate: estimate, isFetchingCoverCleanupEstimate: false });
      }
    }).fail(() => {
      if (this.coverCleanupRequest === pending) {
        this.setState({ coverCleanupEstimateError: true, isFetchingCoverCleanupEstimate: false });
      }
    });
  };

  cancelCoverCleanupEstimate = () => {
    const pending = this.coverCleanupRequest;
    this.coverCleanupRequest = null;
    pending?.abortRequest();
  };

  saveSettings = () => {
    this.cancelCoverCleanupEstimate();
    this.setState({ coverCleanupEstimate: null, isFetchingCoverCleanupEstimate: false, coverCleanupEstimateError: false });

    const coverDownloadScope = this.props.settings?.coverDownloadScope;
    const scopeChanged = coverDownloadScope?.pending && coverDownloadScope.previousValue !== coverDownloadScope.value;
    const mediaManagementSave = this.props.saveMediaManagementSettings();

    this.props.saveNamingSettings();

    if (scopeChanged) {
      mediaManagementSave.done(() => this.props.invalidateBookCoverScopeResources());
    }
  };

  //
  // Render

  render() {
    return (
      <MediaManagement
        {...this.props}
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        onConfirmCoverDownloadScope={this.onConfirmCoverDownloadScope}
        onCancelCoverDownloadScope={this.onCancelCoverDownloadScope}
        onCalculateCoverSavings={this.onCalculateCoverSavings}
        isCoverDownloadScopeConfirmOpen={this.state.isCoverDownloadScopeConfirmOpen}
        coverCleanupEstimate={this.state.coverCleanupEstimate}
        isFetchingCoverCleanupEstimate={this.state.isFetchingCoverCleanupEstimate}
        coverCleanupEstimateError={this.state.coverCleanupEstimateError}
      />
    );
  }
}

MediaManagementConnector.propTypes = {
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  saveNamingSettings: PropTypes.func.isRequired,
  invalidateBookCoverScopeResources: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired,
  fetchDownloadClients: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  settings: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(MediaManagementConnector);
