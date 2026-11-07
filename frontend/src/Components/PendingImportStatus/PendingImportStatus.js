import PropTypes from 'prop-types';
import React from 'react';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';
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

  let content = null;
  if (status === 'pending' || status === 'processing') {
    content = (
      <div className={styles.processing}>
        <LoadingIndicator size={20} />
        <span className={styles.message}>
          {translateServerMessage(message) || translate('PendingImportProcessing')}
        </span>
        {progress > 0 && (
          <span className={styles.progress}>
            {intlFormatters.formatNumber(progress / 100, { style: 'percent', maximumFractionDigits: 0 })}
          </span>
        )}
      </div>
    );
  } else if (status === 'completed') {
    content = <div className={styles.success}>{translate('PendingImportCompleted')}</div>;
  } else if (status === 'failed') {
    content = (
      <div className={styles.error}>
        ✗ {translateServerMessage(error) || translate('PendingImportFailed')}
      </div>
    );
  }

  return <div className={styles.pendingImportStatus}>{content}</div>;
}

PendingImportStatus.propTypes = {
  status: PropTypes.string,
  progress: PropTypes.number,
  message: PropTypes.string,
  error: PropTypes.string
};

export default PendingImportStatus;
