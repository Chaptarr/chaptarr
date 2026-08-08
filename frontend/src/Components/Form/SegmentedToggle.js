import PropTypes from 'prop-types';
import React from 'react';
import styles from './SegmentedToggle.css';

function SegmentedToggle(props) {
  const {
    value,
    options,
    onChange,
    className,
    ariaLabel
  } = props;

  return (
    <div className={[styles.toggleContainer, className].filter(Boolean).join(' ')}>
      <div
        className={styles.toggle}
        role="group"
        aria-label={ariaLabel}
      >
        {
          options.map((option) => {
            const isActive = option.key === value;
            const isDisabled = option.isDisabled === true;

            return (
              <button
                key={option.key}
                type="button"
                aria-pressed={isActive}
                className={[
                  styles.option,
                  isActive ? styles.active : null,
                  isDisabled ? styles.disabled : null
                ].filter(Boolean).join(' ')}
                disabled={isDisabled}
                title={option.title}
                onClick={() => {
                  if (isDisabled || isActive) {
                    return;
                  }

                  onChange(option.key);
                }}
              >
                {option.icon}
                {option.label}
              </button>
            );
          })
        }
      </div>
    </div>
  );
}

SegmentedToggle.propTypes = {
  value: PropTypes.string.isRequired,
  options: PropTypes.arrayOf(PropTypes.shape({
    key: PropTypes.string.isRequired,
    label: PropTypes.node.isRequired,
    icon: PropTypes.node,
    isDisabled: PropTypes.bool,
    title: PropTypes.string
  })).isRequired,
  onChange: PropTypes.func.isRequired,
  className: PropTypes.string,
  ariaLabel: PropTypes.string
};

export default SegmentedToggle;
