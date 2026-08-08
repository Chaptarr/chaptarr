import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchGeneralSettings, saveGeneralSettings, setGeneralSettingsValue } from 'Store/Actions/settingsActions';
import { restart } from 'Store/Actions/systemActions';
import { testProxySettings } from 'Store/Actions/systemActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import GeneralSettings from './GeneralSettings';

const SECTION = 'general';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    createCommandExecutingSelector(commandNames.RESET_API_KEY),
    createSystemStatusSelector(),
    (advancedSettings, sectionSettings, isResettingApiKey, systemStatus) => {
      return {
        advancedSettings,
        isResettingApiKey,
        isWindows: systemStatus.isWindows,
        isWindowsService: systemStatus.isWindows && systemStatus.mode === 'service',
        mode: systemStatus.mode,
        packageUpdateMechanism: systemStatus.packageUpdateMechanism,
        ...sectionSettings
      };
    }
  );
}

const mapDispatchToProps = {
  setGeneralSettingsValue,
  saveGeneralSettings,
  fetchGeneralSettings,
  executeCommand,
  restart,
  clearPendingChanges,
  testProxySettings
};

class GeneralSettingsConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isTestingProxy: false,
      proxyTestError: null
    };
  }

  componentDidMount() {
    this.props.fetchGeneralSettings();
  }

  componentDidUpdate(prevProps) {
    if (!this.props.isResettingApiKey && prevProps.isResettingApiKey) {
      this.props.fetchGeneralSettings();
    }
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: `settings.${SECTION}` });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setGeneralSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.saveGeneralSettings();
  };

  onConfirmResetApiKey = () => {
    this.props.executeCommand({ name: commandNames.RESET_API_KEY });
  };

  onConfirmRestart = () => {
    this.props.restart();
  };

  onTestProxyPress = () => {
    const { settings } = this.props;

    this.setState({ isTestingProxy: true, proxyTestError: null });

    const proxyMode = settings.proxyMode?.value || 'disabled';
    const proxyEnabled = proxyMode !== 'disabled';

    this.props.testProxySettings({
      proxyEnabled,
      proxyType: settings.proxyType?.value || 'http',
      proxyHostname: settings.proxyHostname?.value || '',
      proxyPort: settings.proxyPort?.value || 8080,
      proxyUsername: settings.proxyUsername?.value || '',
      proxyPassword: settings.proxyPassword?.value || ''
    }).then(() => {
      this.setState({ 
        isTestingProxy: false,
        proxyTestError: null 
      });
    }).catch((error) => {
      const errorMessage = error.message || 'Proxy test failed';
      this.setState({ 
        isTestingProxy: false,
        proxyTestError: {
          status: 400,
          responseJSON: [{ message: errorMessage, isWarning: false }]
        }
      });
    });
  };

  //
  // Render

  render() {
    return (
      <GeneralSettings
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        onConfirmResetApiKey={this.onConfirmResetApiKey}
        onConfirmRestart={this.onConfirmRestart}
        isTestingProxy={this.state.isTestingProxy}
        proxyTestError={this.state.proxyTestError}
        onTestProxyPress={this.onTestProxyPress}
        {...this.props}
      />
    );
  }
}

GeneralSettingsConnector.propTypes = {
  isResettingApiKey: PropTypes.bool.isRequired,
  settings: PropTypes.object.isRequired,
  setGeneralSettingsValue: PropTypes.func.isRequired,
  saveGeneralSettings: PropTypes.func.isRequired,
  fetchGeneralSettings: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  restart: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired,
  testProxySettings: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(GeneralSettingsConnector);
