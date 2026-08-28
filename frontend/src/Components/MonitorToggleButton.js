import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import { icons } from 'Helpers/Props';
import styles from './MonitorToggleButton.css';

function getTooltip(monitored, isDisabled, disabledTooltip) {
  if (isDisabled) {
    return disabledTooltip || 'Cannot toggle monitored state';
  }

  if (monitored) {
    return 'Monitored - Click to stop monitoring';
  }
  return 'Not monitored - Click to monitor';
}

function getNextState(currentState) {
  return !currentState;
}

class MonitorToggleButton extends Component {

  //
  // Listeners

  onPress = (event) => {
    const shiftKey = event.nativeEvent.shiftKey;
    const nextState = getNextState(this.props.monitored);
    this.props.onPress(nextState, { shiftKey });
  };

  //
  // Render

  renderIcon() {
    const { monitored } = this.props;

    if (monitored) {
      return icons.MONITORED;
    }
    return icons.UNMONITORED;
  }

  render() {
    const {
      className,
      monitored,
      isDisabled,
      isSaving,
      size,
      disabledTooltip,
      ...otherProps
    } = this.props;

    const iconElement = this.renderIcon();

    return (
      <SpinnerIconButton
        className={classNames(
          className,
          isDisabled && styles.isDisabled
        )}
        name={iconElement}
        size={size}
        title={getTooltip(monitored, isDisabled, disabledTooltip)}
        isDisabled={isDisabled}
        isSpinning={isSaving}
        {...otherProps}
        onPress={this.onPress}
      />
    );
  }
}

MonitorToggleButton.propTypes = {
  className: PropTypes.string.isRequired,
  monitored: PropTypes.bool.isRequired,
  size: PropTypes.number,
  isDisabled: PropTypes.bool.isRequired,
  disabledTooltip: PropTypes.string,
  isSaving: PropTypes.bool.isRequired,
  onPress: PropTypes.func.isRequired
};

MonitorToggleButton.defaultProps = {
  className: styles.toggleButton,
  isDisabled: false,
  isSaving: false
};

export default MonitorToggleButton;
