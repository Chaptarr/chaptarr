import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchGeneralSettings, saveGeneralSettings, setGeneralSettingsValue } from 'Store/Actions/settingsActions';
import { fetchStatus } from 'Store/Actions/systemActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import AuthenticationRequiredModalContent from './AuthenticationRequiredModalContent';

const SECTION = 'general';

function createMapStateToProps() {
  return createSelector(
    createSettingsSectionSelector(SECTION),
    (sectionSettings) => {
      return {
        ...sectionSettings
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchClearPendingChanges: clearPendingChanges,
  dispatchSetGeneralSettingsValue: setGeneralSettingsValue,
  dispatchSaveGeneralSettings: saveGeneralSettings,
  dispatchFetchGeneralSettings: fetchGeneralSettings,
  dispatchFetchStatus: fetchStatus
};

class AuthenticationRequiredModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchGeneralSettings();
  }

  componentDidUpdate(prevProps) {
    // Once auth is enabled, reload immediately so the browser enters the selected auth flow
    // (Plex/SSO redirect or local login), instead of allowing the already-loaded SPA to keep running.
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      const urlBase = (window.Chaptarr && window.Chaptarr.urlBase) || '';
      const authMethod = String(this.props.settings?.authenticationMethod?.value || '').toLowerCase();

      if (authMethod === 'plex') {
        window.location = `${urlBase}/auth/plex?returnUrl=${encodeURIComponent('/')}`;
        return;
      }

      if (authMethod === 'oidc') {
        window.location = `${urlBase}/auth/oidc?returnUrl=${encodeURIComponent('/')}`;
        return;
      }

      if (authMethod === 'forms') {
        // If we just saved Forms credentials, the backend issues a Forms auth cookie during the save.
        // Navigate to the app root and let the server decide whether to show the UI or redirect to /login.
        window.location = `${urlBase}/`;
        return;
      }

      // Basic auth uses the browser prompt; just reload into an authenticated route.
      if (authMethod === 'basic') {
        window.location = `${urlBase}/`;
        return;
      }

      window.location.reload();
    }
  }

  componentWillUnmount() {
    this.props.dispatchClearPendingChanges({ section: `settings.${SECTION}` });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetGeneralSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.dispatchSaveGeneralSettings();
  };

  //
  // Render

  render() {
    const {
      dispatchClearPendingChanges,
      dispatchFetchGeneralSettings,
      dispatchSetGeneralSettingsValue,
      dispatchSaveGeneralSettings,
      ...otherProps
    } = this.props;

    return (
      <AuthenticationRequiredModalContent
        {...otherProps}
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
      />
    );
  }
}

AuthenticationRequiredModalContentConnector.propTypes = {
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  dispatchFetchGeneralSettings: PropTypes.func.isRequired,
  dispatchSetGeneralSettingsValue: PropTypes.func.isRequired,
  dispatchSaveGeneralSettings: PropTypes.func.isRequired,
  dispatchFetchStatus: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthenticationRequiredModalContentConnector);
