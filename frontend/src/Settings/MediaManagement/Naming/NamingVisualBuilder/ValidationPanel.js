import React from 'react';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Icon from 'Components/Icon';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ValidationPanel.css';

function ValidationPanel({ validation, isLoading }) {
  if (isLoading) {
    return (
      <div className={styles.panel}>
        <LoadingIndicator size={16} />
        <span className={styles.loadingText}>{translate('NamingBuilderValidatingPattern')}</span>
      </div>
    );
  }

  if (!validation.errors || validation.errors.length === 0) {
    return (
      <div className={`${styles.panel} ${styles.success}`}>
        <Icon name={icons.CHECK} size={16} />
        <span>{translate('NamingBuilderPatternIsValid')}</span>
      </div>
    );
  }

  const errorsByType = validation.errors.reduce((acc, error) => {
    const type = error.code || 'GENERAL';
    if (!acc[type]) {
      acc[type] = [];
    }
    acc[type].push(error);
    return acc;
  }, {});

  return (
    <div className={styles.panel}>
      {Object.entries(errorsByType).map(([type, errors]) => (
        <Alert key={type} kind={kinds.DANGER} className={styles.errorGroup}>
          <div className={styles.errorHeader}>
            <Icon name={icons.DANGER} size={14} />
            <span className={styles.errorType}>{getErrorTypeLabel(type)}</span>
            <span className={styles.errorCount}>({errors.length})</span>
          </div>

          <ul className={styles.errorList}>
            {errors.map((error, index) => (
              <li key={index} className={styles.errorItem}>
                {error.message}
                {error.path && (
                  <span className={styles.errorPath}>
                    {translate('NamingBuilderErrorAtPath', { path: error.path })}
                  </span>
                )}
              </li>
            ))}
          </ul>
        </Alert>
      ))}
    </div>
  );
}

function getErrorTypeLabel(type) {
  const labels = {
    EMPTY_PATTERN: translate('NamingBuilderErrorEmptyPattern'),
    CONSECUTIVE_FOLDERS: translate('NamingBuilderErrorFolderStructure'),
    EMPTY_GROUP: translate('NamingBuilderErrorEmptyGroups'),
    INVALID_TOKEN: translate('NamingBuilderErrorInvalidTokens'),
    VALIDATION_ERROR: translate('NamingBuilderErrorValidationError'),
    EXCEPTION: translate('NamingBuilderErrorSystemError'),
    GENERAL: translate('NamingBuilderErrorGeneral')
  };

  return labels[type] || type;
}

export default ValidationPanel;
