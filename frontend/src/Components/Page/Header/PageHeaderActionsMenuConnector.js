import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Button from 'Components/Link/Button';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import { restart, shutdown } from 'Store/Actions/systemActions';
import translate from 'Utilities/String/translate';
import PageHeaderActionsMenu from './PageHeaderActionsMenu';
import styles from './PageHeaderActionsMenu.css';

function createMapStateToProps() {
  return createSelector(
    (state) => state.system.status,
    (status) => {
      return {
        formsAuth: status.item.authentication === 'forms'
      };
    }
  );
}

class PageHeaderActionsMenuConnector extends Component {

  //
  // Listeners

  onRestartPress = () => {
    this.props.restart();
  };

  onShutdownPress = () => {
    this.props.shutdown();
  };

  onExperienceModePress = () => {
    const experienceMode = this.props.experienceMode === 'simple' ? 'advanced' : 'simple';
    this.props.setUISettingsValue({ name: 'experienceMode', value: experienceMode });
    this.props.saveUISettings();
  };

  //
  // Render

  render() {
    const {
      experienceMode,
      ...menuProps
    } = this.props;

    const isSimpleExperience = experienceMode === 'simple';
    const nextExperienceModeLabel = isSimpleExperience ?
      'SwitchToAdvancedExperience' :
      'SwitchToSimpleExperience';
    const experienceModeButtonLabel = isSimpleExperience ?
      'SimpleExperienceToggleLabel' :
      'AdvancedExperienceToggleLabel';

    return (
      <>
        <Button
          className={styles.experienceModeButton}
          aria-label={translate(experienceModeButtonLabel)}
          aria-pressed={isSimpleExperience}
          title={translate(nextExperienceModeLabel)}
          onPress={this.onExperienceModePress}
        >
          {translate(isSimpleExperience ? 'SimpleModeLabel' : 'AdvancedModeLabel')}
        </Button>

        <PageHeaderActionsMenu
          {...menuProps}
          onRestartPress={this.onRestartPress}
          onShutdownPress={this.onShutdownPress}
        />
      </>
    );
  }
}

PageHeaderActionsMenuConnector.propTypes = {
  restart: PropTypes.func.isRequired,
  shutdown: PropTypes.func.isRequired,
  experienceMode: PropTypes.oneOf(['simple', 'advanced']).isRequired,
  setUISettingsValue: PropTypes.func.isRequired,
  saveUISettings: PropTypes.func.isRequired
};

const mapDispatchToProps = {
  restart,
  shutdown,
  setUISettingsValue,
  saveUISettings
};

export default connect(createMapStateToProps, mapDispatchToProps)(PageHeaderActionsMenuConnector);
