import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import LinearProgressBar from 'Components/LinearProgressBar';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ImportProgressDrawerModal.css';

function percent(processed, total) {
  if (!total || total <= 0) {
    return 0;
  }

  return Math.min(100, Math.round((processed / total) * 100));
}

function formatStage(stage) {
  if (!stage) {
    return translate('ImportPreparing');
  }

  return stage
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/_/g, ' ');
}

function getProgressTitle(operation) {
  switch (operation) {
    case 'RefreshUnmappedFiles':
      return translate('UnmappedFilesRefreshFiles');
    case 'RetryUnmappedMatch':
      return translate('UnmappedFilesRetryMatch');
    default:
      return translate('LibraryImport');
  }
}

function ImportProgressDrawerModal(props) {
  const {
    isOpen,
    onModalClose,
    importTracker,
    isPaused,
    onPauseResume
  } = props;

  const counters = importTracker?.counters || {};
  const processedAuthors = counters.processedAuthorFolders || 0;
  const totalAuthors = counters.totalAuthorFolders || 0;
  const processedBooks = counters.processedBookFolders || 0;
  const totalBooks = counters.totalBookFolders || 0;

  const authorsImported = counters.authorsImported || 0;
  const booksImported = counters.matchedBooks || 0;
  const filesImported = counters.filesImported || 0;

  const stageText = importTracker?.message || formatStage(importTracker?.stage);
  const currentItemName = importTracker?.currentItemName;
  const showAuthorProgress = totalAuthors > 0 || processedAuthors > 0;
  const progressTitle = getProgressTitle(importTracker?.operation);

  return (
    <Modal
      isOpen={isOpen}
      onModalClose={onModalClose}
      closeOnBackgroundClick={true}
      backdropClassName={styles.backdrop}
      className={styles.drawer}
      size={sizes.MEDIUM}
    >
      <ModalContent
        onModalClose={onModalClose}
      >
        <ModalHeader>
          <div className={styles.headerRow}>
            <div className={styles.headerText}>
              <div className={styles.title}>{progressTitle}</div>
              <div className={styles.subtitle}>{stageText}</div>
            </div>

            {onPauseResume ? (
              <Button
                kind={isPaused ? kinds.SUCCESS : kinds.DEFAULT}
                size={sizes.SMALL}
                onPress={onPauseResume}
              >
                <Icon name={isPaused ? icons.PLAY : icons.PAUSE} />
                {' '}
                {isPaused ? translate('Resume') : translate('Pause')}
              </Button>
            ) : null}
          </div>
        </ModalHeader>

        <ModalBody>
          <div className={styles.section}>
            <div className={styles.sectionTitle}>{translate('Progress')}</div>

            {showAuthorProgress ? (
              <>
                <div className={styles.row}>
                  <div className={styles.rowLabel}>
                    <Icon name={icons.AUTHOR} />
                    <span>{translate('ImportProgressAuthorsProcessed')}</span>
                  </div>
                  <div className={styles.rowCounts}>
                    {processedAuthors}/{totalAuthors}
                  </div>
                </div>
                <LinearProgressBar
                  progress={percent(processedAuthors, totalAuthors)}
                  showProgressText={false}
                  kind="success"
                  size="small"
                />
              </>
            ) : null}

            <div className={styles.row} style={{ marginTop: showAuthorProgress ? 12 : 0 }}>
              <div className={styles.rowLabel}>
                <Icon name={icons.BOOK} />
                <span>{translate('ImportProgressBookUnitsProcessed')}</span>
              </div>
              <div className={styles.rowCounts}>
                {processedBooks}/{totalBooks}
              </div>
            </div>
            <LinearProgressBar
              progress={percent(processedBooks, totalBooks)}
              showProgressText={false}
              kind="success"
              size="small"
            />
          </div>

          <div className={styles.summaryGrid}>
            <div className={styles.summaryCard}>
              <div className={styles.summaryLabel}>{translate('ImportProgressAuthorsImported')}</div>
              <div className={styles.summaryValue}>{authorsImported}</div>
            </div>
            <div className={styles.summaryCard}>
              <div className={styles.summaryLabel}>{translate('ImportProgressBooksImported')}</div>
              <div className={styles.summaryValue}>{booksImported}</div>
            </div>
            <div className={styles.summaryCard}>
              <div className={styles.summaryLabel}>{translate('ImportProgressFilesImported')}</div>
              <div className={styles.summaryValue}>{filesImported}</div>
            </div>
            <div className={styles.summaryCard}>
              <div className={styles.summaryLabel}>{translate('ImportProgressOverallProgress')}</div>
              <div className={styles.summaryValue}>{Math.round(importTracker?.progress || 0)}%</div>
            </div>
          </div>

          <div className={styles.currentItem}>
            <div className={styles.currentItemLabel}>{translate('ImportProgressCurrentlyProcessing')}</div>
            <div className={styles.currentItemValue}>{currentItemName || translate('ImportProgressScanning')}</div>
          </div>
        </ModalBody>
      </ModalContent>
    </Modal>
  );
}

ImportProgressDrawerModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  importTracker: PropTypes.object,
  isPaused: PropTypes.bool,
  onPauseResume: PropTypes.func
};

ImportProgressDrawerModal.defaultProps = {
  isPaused: false
};

export default ImportProgressDrawerModal;
