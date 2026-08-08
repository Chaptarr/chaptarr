import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import {
  fetchConversionSettings,
  saveConversionSettings,
  setConversionSettingsValue
} from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import ConversionSettings from './ConversionSettings';

const SECTION = 'conversion';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    (advancedSettings, sectionSettings) => {
      return {
        advancedSettings,
        ...sectionSettings,
        hasPendingChanges: sectionSettings.hasPendingChanges
      };
    }
  );
}

const mapDispatchToProps = {
  fetchConversionSettings,
  setConversionSettingsValue,
  saveConversionSettings,
  clearPendingChanges
};

class ConversionSettingsConnector extends Component {

  componentDidMount() {
    this.props.fetchConversionSettings();
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: `settings.${SECTION}` });
  }

  onInputChange = ({ name, value }) => {
    this.props.setConversionSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.saveConversionSettings();
  };

  render() {
    return (
      <ConversionSettings
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        {...this.props}
      />
    );
  }
}

ConversionSettingsConnector.propTypes = {
  fetchConversionSettings: PropTypes.func.isRequired,
  setConversionSettingsValue: PropTypes.func.isRequired,
  saveConversionSettings: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ConversionSettingsConnector);
