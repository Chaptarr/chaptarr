import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import { icons } from 'Helpers/Props';
import styles from './MonitorToggleButton.css';

function getTooltip(monitored, isDisabled, isBinary, disabledTooltip) {
  if (isDisabled) {
    return disabledTooltip || 'Cannot toggle monitored state when author is unmonitored';
  }

  // Binary mode (books) - simple on/off
  if (isBinary) {
    if (monitored === true || monitored === 1) {
      return 'Monitored - Click to stop monitoring';
    }
    return 'Not monitored - Click to monitor';
  }

  // Tri-state mode (authors) - show current state and next state
  if (monitored === 0 || monitored === false) {
    return 'Currently: None - Click to change to: Monitor All';
  } else if (monitored === 1 || monitored === true) {
    return 'Currently: Monitor All - Click to change to: Monitor Selected';
  } else if (monitored === 2) {
    return 'Currently: Monitor Selected - Click to change to: None';
  }

  // Fallback
  return 'Currently: Not monitored - Click to monitor';
}

function getNextState(currentState, isBinary) {
  // Binary mode (books) - simple toggle
  if (isBinary) {
    return (currentState === true || currentState === 1) ? false : true;
  }

  // Tri-state mode (authors) - cycle through states: None (0) -> All (1) -> Selected (2) -> None (0)
  if (currentState === 0 || currentState === false) {
    return 1; // Next: All
  } else if (currentState === 1 || currentState === true) {
    return 2; // Next: Selected
  } else if (currentState === 2) {
    return 0; // Next: None
  }
  return 1; // Default to All
}

class MonitorToggleButton extends Component {

  //
  // Listeners

  onPress = (event) => {
    const shiftKey = event.nativeEvent.shiftKey;
    const nextState = getNextState(this.props.monitored, this.props.isBinary);
    this.props.onPress(nextState, { shiftKey });
  };

  //
  // Render

  renderIcon() {
    const { monitored, size, isBinary } = this.props;
    
    // Convert size to px string for consistent sizing
    const sizeInPx = size ? `${size}px` : '24px';
    
    // Binary mode (books) - only filled or outline
    if (isBinary) {
      if (monitored === true || monitored === 1) {
        return icons.MONITORED; // Filled bookmark
      }
      return icons.UNMONITORED; // Outline bookmark
    }
    
    // Tri-state mode (authors) - outline, filled, or layered
    if (monitored === 0 || monitored === false) {
      // None: outline bookmark
      return icons.UNMONITORED;
    } else if (monitored === 2) {
      // Selected: layered bookmark (grey outer with red inner)
      // Return a React element instead of just an icon object
      return (
        <span className="fa-layers fa-fw" style={{ width: sizeInPx, height: sizeInPx, fontSize: sizeInPx }}>
          <FontAwesomeIcon 
            icon={icons.MONITORED} 
            style={{ 
              color: 'var(--defaultIconColor, #656565)',
              fontSize: sizeInPx
            }} 
          />
          <FontAwesomeIcon 
            icon={icons.MONITORED} 
            transform="shrink-8" 
            style={{ 
              color: 'var(--chaptarrRed, #e74c3c)',
              fontSize: sizeInPx
            }} 
          />
        </span>
      );
    } else {
      // All (1 or true): solid bookmark
      return icons.MONITORED;
    }
  }

  render() {
    const {
      className,
      monitored,
      isDisabled,
      isSaving,
      size,
      isBinary,
      disabledTooltip,
      ...otherProps
    } = this.props;

    const iconElement = this.renderIcon();
    const sizeInPx = size ? `${size}px` : '24px';
    
    // If we're rendering a layered icon (for Selected state), 
    // we need to handle it differently and match the size of other states
    // Note: Binary mode will never reach here since it doesn't have state 2
    if (monitored === 2 && !isBinary) {
      return (
        <button
          className={classNames(
            styles.toggleButton,
            className,
            isDisabled && styles.isDisabled
          )}
          style={{
            width: sizeInPx,
            height: sizeInPx,
            fontSize: sizeInPx,
            lineHeight: sizeInPx
          }}
          disabled={isDisabled || isSaving}
          title={getTooltip(monitored, isDisabled, isBinary, disabledTooltip)}
          onClick={this.onPress}
        >
          {isSaving ? (
            <FontAwesomeIcon icon={icons.SPINNER} spin style={{ fontSize: sizeInPx }} />
          ) : (
            iconElement
          )}
        </button>
      );
    }

    // For normal states, use the standard SpinnerIconButton (which already works correctly)
    return (
      <SpinnerIconButton
        className={classNames(
          className,
          isDisabled && styles.isDisabled
        )}
        name={iconElement}
        size={size}
        title={getTooltip(monitored, isDisabled, isBinary, disabledTooltip)}
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
  monitored: PropTypes.oneOfType([PropTypes.bool, PropTypes.number]).isRequired,
  size: PropTypes.number,
  isDisabled: PropTypes.bool.isRequired,
  disabledTooltip: PropTypes.string,
  isSaving: PropTypes.bool.isRequired,
  isBinary: PropTypes.bool,
  onPress: PropTypes.func.isRequired
};

MonitorToggleButton.defaultProps = {
  className: styles.toggleButton,
  isDisabled: false,
  isSaving: false,
  isBinary: false  // Default to tri-state for backward compatibility
};

export default MonitorToggleButton;
