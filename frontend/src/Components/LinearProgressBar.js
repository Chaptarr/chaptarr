import PropTypes from 'prop-types';
import React from 'react';
import styles from './LinearProgressBar.css';

function LinearProgressBar(props) {
  const {
    progress,
    className,
    containerClassName,
    showProgressText
  } = props;

  // Ensure progress is between 0 and 100
  const clampedProgress = Math.max(0, Math.min(100, progress || 0));

  return (
    <div className={containerClassName}>
      <div className={className}>
        <div 
          className={styles.progressFill}
          style={{ width: `${clampedProgress}%` }}
        />
      </div>
      {showProgressText && (
        <div className={styles.progressText}>
          {`${Math.round(clampedProgress)}%`}
        </div>
      )}
    </div>
  );
}

LinearProgressBar.propTypes = {
  progress: PropTypes.number.isRequired,
  className: PropTypes.string,
  containerClassName: PropTypes.string,
  showProgressText: PropTypes.bool
};

LinearProgressBar.defaultProps = {
  className: styles.linearProgressBar,
  containerClassName: styles.linearProgressBarContainer,
  showProgressText: false
};

export default LinearProgressBar;