import PropTypes from 'prop-types';
import React from 'react';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import translate from 'Utilities/String/translate';
import styles from './PendingImportStatus.css';

function PendingImportStatus(props) {
  const {
    status,
    progress,
    message,
    error
  } = props;

  if (!status) {
    return null;
  }

  return (
    <div className={styles.pendingImportStatus}>
      {status === 'pending' || status === 'processing' ? (
        <div className={styles.processing}>
          <LoadingIndicator size={20} />
          <span className={styles.message}>
            {message || 'Processing import...'}
          </span>
          {progress > 0 && (
            <span className={styles.progress}>
              {progress}%
            </span>
          )}
        </div>
      ) : status === 'completed' ? (
        <div className={styles.success}>
          {translate('PendingImportCompleted')}
        </div>
      ) : status === 'failed' ? (
        <div className={styles.error}>
          ✗ {error || translate('PendingImportFailed')}
        </div>
      ) : null}
    </div>
  );
}

PendingImportStatus.propTypes = {
  status: PropTypes.string,
  progress: PropTypes.number,
  message: PropTypes.string,
  error: PropTypes.string
};

export default PendingImportStatus;