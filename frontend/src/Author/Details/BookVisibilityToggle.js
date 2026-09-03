import PropTypes from 'prop-types';
import React from 'react';
import { connect } from 'react-redux';
import { toggleHideUnmonitoredMissing } from 'Store/Actions/appActions';
import translate from 'Utilities/String/translate';
import styles from './BookVisibilityToggle.css';

function BookVisibilityToggle(props) {
  const {
    hideUnmonitoredMissing,
    onTogglePress
  } = props;

  const isShowingAll = !hideUnmonitoredMissing;
  const isShowingLibrary = hideUnmonitoredMissing;
  const helpText = translate('BookVisibilityToggleHelpText');

  return (
    <div
      className={styles.container}
      role="group"
      aria-label={helpText}
    >
      <div className={`${styles.slider} ${isShowingAll ? styles.sliderAll : ''}`} />
      <button
        type="button"
        className={`${styles.option} ${isShowingLibrary ? styles.active : ''}`}
        aria-pressed={isShowingLibrary}
        onClick={isShowingLibrary ? undefined : onTogglePress}
      >
        {translate('Library')}
      </button>

      <button
        type="button"
        className={`${styles.option} ${isShowingAll ? styles.active : ''}`}
        aria-pressed={isShowingAll}
        onClick={isShowingAll ? undefined : onTogglePress}
      >
        {translate('All')}
      </button>
    </div>
  );
}

BookVisibilityToggle.propTypes = {
  hideUnmonitoredMissing: PropTypes.bool.isRequired,
  onTogglePress: PropTypes.func.isRequired
};

function createMapStateToProps() {
  return (state) => {
    return {
      hideUnmonitoredMissing: state.app.hideUnmonitoredMissing
    };
  };
}

const mapDispatchToProps = {
  onTogglePress: toggleHideUnmonitoredMissing
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookVisibilityToggle);
