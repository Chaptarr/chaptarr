import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { useState } from 'react';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import ImportProgressDrawerModal from './ImportProgressDrawerModal';
import styles from './ImportProgressIndicator.css';

function ImportProgressIndicator(props) {
  const {
    importTracker,
    isActive,
    isPaused,
    onPauseResume
  } = props;

  const [isOpen, setIsOpen] = useState(false);

  if (!isActive) {
    return null;
  }

  const counters = importTracker?.counters || {};
  const processedAuthors = counters.processedAuthorFolders || 0;
  const totalAuthors = counters.totalAuthorFolders || 0;
  const processedBooks = counters.processedBookFolders || 0;
  const totalBooks = counters.totalBookFolders || 0;
  const progress = Math.max(0, Math.min(100, Math.round(importTracker?.progress || 0)));
  const isComplete = importTracker?.stage === 'ImportComplete' || progress >= 100;

  const onChipClick = () => {
    setIsOpen(true);
  };

  const onChipKeyDown = (event) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      setIsOpen(true);
    }
  };

  const onPauseClick = (event) => {
    event.preventDefault();
    event.stopPropagation();

    if (onPauseResume) {
      onPauseResume();
    }
  };

  return (
      <div className={styles.container}>
      <div
        className={classNames(
          styles.chip,
          isPaused && styles.chipPaused
        )}
        style={{ '--progress': progress }}
        onClick={onChipClick}
        onKeyDown={onChipKeyDown}
        role="button"
        tabIndex={0}
      >
        <Icon
          name={isPaused ? icons.PAUSED : icons.SPINNER}
          className={styles.statusIcon}
          isSpinning={!isPaused && !isComplete}
        />

        <div className={styles.counts}>
          <div className={styles.countItem}>
            <Icon name={icons.AUTHOR} className={styles.countIcon} />
            <span className={styles.countLabel}>{translate('Authors')}</span>
            <span className={styles.countValue}>{processedAuthors}/{totalAuthors}</span>
          </div>

          <div className={styles.countItem}>
            <Icon name={icons.BOOK} className={styles.countIcon} />
            <span className={styles.countLabel}>{translate('Units')}</span>
            <span className={styles.countValue}>{processedBooks}/{totalBooks}</span>
          </div>
        </div>

        {onPauseResume ? (
          <IconButton
            className={styles.pauseButton}
            name={isPaused ? icons.PLAY : icons.PAUSE}
            title={isPaused ? translate('ResumeImport') : translate('PauseImport')}
            onPress={onPauseClick}
            size={14}
          />
        ) : null}
      </div>

      <ImportProgressDrawerModal
        isOpen={isOpen}
        onModalClose={() => setIsOpen(false)}
        importTracker={importTracker}
        isPaused={isPaused}
        onPauseResume={onPauseResume}
      />
    </div>
  );
}

ImportProgressIndicator.propTypes = {
  importTracker: PropTypes.object,
  isActive: PropTypes.bool.isRequired,
  isPaused: PropTypes.bool.isRequired,
  onPauseResume: PropTypes.func
};

export default ImportProgressIndicator;
