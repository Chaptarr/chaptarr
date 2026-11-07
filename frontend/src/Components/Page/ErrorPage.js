import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate, { translateWithFallback } from 'Utilities/String/translate';
import styles from './ErrorPage.css';

function ErrorPage(props) {
  const {
    version,
    isLocalStorageSupported,
    translationsError,
    authorError,
    customFiltersError,
    tagsError,
    languagesError,
    qualityProfilesError,
    metadataProfilesError,
    importListsError,
    indexerFlagsError,
    uiSettingsError,
    systemStatusError,
    onRetryPress
  } = props;

  let errorMessage = translateWithFallback('ErrorPageLoadFailed', 'Failed to load Chaptarr');

  if (!isLocalStorageSupported) {
    errorMessage = translateWithFallback(
      'ErrorPageLocalStorageUnavailable',
      'Local Storage is not supported or disabled. A plugin or private browsing may have disabled it.'
    );
  } else if (translationsError) {
    errorMessage = getErrorMessage(
      translationsError,
      translateWithFallback('ErrorPageTranslationsUnavailable', 'Failed to load translations from API')
    );
  } else if (authorError) {
    errorMessage = getErrorMessage(
      authorError,
      translateWithFallback('ErrorPageAuthorUnavailable', 'Failed to load author from API')
    );
  } else if (customFiltersError) {
    errorMessage = getErrorMessage(
      customFiltersError,
      translateWithFallback('ErrorPageCustomFiltersUnavailable', 'Failed to load custom filters from API')
    );
  } else if (tagsError) {
    errorMessage = getErrorMessage(
      tagsError,
      translateWithFallback('ErrorPageTagsUnavailable', 'Failed to load tags from API')
    );
  } else if (languagesError) {
    errorMessage = getErrorMessage(
      languagesError,
      translateWithFallback('ErrorPageLanguagesUnavailable', 'Failed to load languages from API')
    );
  } else if (qualityProfilesError) {
    errorMessage = getErrorMessage(
      qualityProfilesError,
      translateWithFallback('ErrorPageQualityProfilesUnavailable', 'Failed to load quality profiles from API')
    );
  } else if (metadataProfilesError) {
    errorMessage = getErrorMessage(
      metadataProfilesError,
      translateWithFallback('ErrorPageMetadataProfilesUnavailable', 'Failed to load metadata profiles from API')
    );
  } else if (importListsError) {
    errorMessage = getErrorMessage(
      importListsError,
      translateWithFallback('ErrorPageImportListsUnavailable', 'Failed to load import lists from API')
    );
  } else if (indexerFlagsError) {
    errorMessage = getErrorMessage(
      indexerFlagsError,
      translateWithFallback('ErrorPageIndexerFlagsUnavailable', 'Failed to load indexer flags from API')
    );
  } else if (uiSettingsError) {
    errorMessage = getErrorMessage(
      uiSettingsError,
      translateWithFallback('ErrorPageUISettingsUnavailable', 'Failed to load UI settings from API')
    );
  } else if (systemStatusError) {
    errorMessage = getErrorMessage(
      systemStatusError,
      translateWithFallback('ErrorPageSystemStatusUnavailable', 'Failed to load system status from API')
    );
  }

  return (
    <div className={styles.page}>
      <div className={styles.errorMessage}>
        {errorMessage}
      </div>

      {
        isLocalStorageSupported &&
          <div className={styles.retry}>
            <Button onPress={onRetryPress}>
              {translateWithFallback('Retry', 'Retry')}
            </Button>
          </div>
      }

      <div className={styles.version}>
        {translate('ErrorPageVersion', { version })}
      </div>
    </div>
  );
}

ErrorPage.propTypes = {
  version: PropTypes.string.isRequired,
  isLocalStorageSupported: PropTypes.bool.isRequired,
  translationsError: PropTypes.object,
  authorError: PropTypes.object,
  customFiltersError: PropTypes.object,
  tagsError: PropTypes.object,
  languagesError: PropTypes.object,
  qualityProfilesError: PropTypes.object,
  metadataProfilesError: PropTypes.object,
  importListsError: PropTypes.object,
  indexerFlagsError: PropTypes.object,
  uiSettingsError: PropTypes.object,
  systemStatusError: PropTypes.object,
  onRetryPress: PropTypes.func.isRequired
};

export default ErrorPage;
