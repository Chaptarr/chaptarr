import classNames from 'classnames';
import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import { icons } from 'Helpers/Props';
import styles from './PageToolbarStatusButton.css';

function PageToolbarStatusButton(props) {
  const {
    label,
    iconName,
    isEnabled,
    enabledTitle,
    disabledTitle,
    onPress,
    showLabel,
    isDisabled
  } = props;

  return (
    <Link
      className={classNames(
        styles.button,
        isDisabled && styles.isDisabled
      )}
      title={isEnabled ? enabledTitle : disabledTitle}
      isDisabled={isDisabled}
      onPress={onPress}
    >
      <Icon
        name={iconName}
        size={21}
      />

      <span
        className={classNames(
          styles.indicatorContainer,
          'fa-layers fa-fw'
        )}
      >
        <Icon
          className={styles.indicatorBackground}
          name={icons.CIRCLE}
          size={16}
        />

        <Icon
          className={isEnabled ? styles.enabled : styles.disabled}
          name={isEnabled ? icons.CHECK : icons.CLOSE}
          size={10}
        />
      </span>

      {
        showLabel ?
          <div className={styles.labelContainer}>
            <div className={styles.label}>
              {label}
            </div>
          </div> :
          null
      }
    </Link>
  );
}

PageToolbarStatusButton.propTypes = {
  label: PropTypes.string.isRequired,
  iconName: PropTypes.object.isRequired,
  isEnabled: PropTypes.bool.isRequired,
  enabledTitle: PropTypes.string.isRequired,
  disabledTitle: PropTypes.string.isRequired,
  onPress: PropTypes.func.isRequired,
  showLabel: PropTypes.bool,
  isDisabled: PropTypes.bool
};

PageToolbarStatusButton.defaultProps = {
  showLabel: true,
  isDisabled: false
};

export default PageToolbarStatusButton;
