import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import { kinds } from 'Helpers/Props';
import { getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import { getMediaTypesForScope, getQuickstartStatus, hasRootFolderForScope } from './QuickstartSetupUtils';
import styles from './QuickstartSetup.css';

function getStatusLabel(status) {
  const labels = {
    configured: 'QuickstartStatusConfigured',
    skipped: 'QuickstartStatusSkipped',
    failed: 'QuickstartStatusFailed',
    needed: 'QuickstartStatusNeeded'
  };

  return translate(labels[status]);
}

function getMediaScopeLabel(mediaScope) {
  if (mediaScope === 'both') {
    return translate('Both');
  }

  if (mediaScope === 'ebook') {
    return translate('Ebooks');
  }

  if (mediaScope === 'audiobook') {
    return translate('Audiobooks');
  }

  return translate('QuickstartStatusNeeded');
}

function QuickstartReviewSection(props) {
  const {
    mediaScope,
    rootFolders,
    indexers,
    downloadClients,
    showAudioBookShelf,
    showGrimmory,
    showHardcover,
    hasActiveAudioBookShelf,
    hasActiveGrimmory,
    isHardcoverConfigured,
    isProxyConfigured,
    interactions,
    connectionResults,
    schemaErrors,
    isGuidedMode,
    onReviewComplete
  } = props;

  const getRowStatus = (configured, failed, skipped) => getQuickstartStatus({ configured, failed, skipped });
  const optionalWasReviewed = !!interactions.optionalConnections;
  const mediaTypes = getMediaTypesForScope(mediaScope);
  const rootFoldersForScope = rootFolders.filter((folder) =>
    mediaTypes.some((mediaType) => getRootFolderMediaTypes(folder).includes(mediaType))
  );
  const calibreRootFolders = rootFoldersForScope.filter((folder) =>
    folder.isCalibreLibrary && getRootFolderMediaTypes(folder).includes('ebook')
  );

  const rows = [
    {
      label: translate('QuickstartReviewFormats'),
      value: getMediaScopeLabel(mediaScope),
      status: getRowStatus(!!mediaScope, false, false)
    },
    {
      label: translate('QuickstartReviewRootFolders'),
      value: rootFoldersForScope.length ? rootFoldersForScope.map((folder) => folder.path || folder.name).join(', ') : translate('QuickstartNoneConfigured'),
      status: getRowStatus(
        hasRootFolderForScope(rootFolders, mediaScope),
        false,
        !!interactions.rootFolders
      )
    },
    {
      label: translate('QuickstartReviewIndexers'),
      value: indexers.length ? indexers.map((indexer) => indexer.name).join(', ') : translate('QuickstartNoneConfigured'),
      status: getRowStatus(indexers.length > 0, !!schemaErrors.indexers, !!interactions.indexers)
    },
    {
      label: translate('QuickstartReviewDownloadClients'),
      value: downloadClients.length ? downloadClients.map((client) => client.name).join(', ') : translate('QuickstartNoneConfigured'),
      status: getRowStatus(downloadClients.length > 0, !!schemaErrors.downloadClients, !!interactions.downloadClients)
    },
    ...(mediaTypes.includes('ebook') ? [{
      label: 'Calibre',
      value: calibreRootFolders.length ? calibreRootFolders.map((folder) => folder.path || folder.name).join(', ') : translate('QuickstartNoneConfigured'),
      status: getRowStatus(calibreRootFolders.length > 0, false, hasRootFolderForScope(rootFolders, mediaScope) || !!interactions.rootFolders)
    }] : []),
    ...(showAudioBookShelf ? [{
      label: 'AudioBookShelf',
      status: getRowStatus(hasActiveAudioBookShelf, !!connectionResults.audioBookShelf || (!!schemaErrors.notifications && !hasActiveAudioBookShelf), optionalWasReviewed)
    }] : []),
    ...(showGrimmory ? [{
      label: 'Grimmory',
      status: getRowStatus(hasActiveGrimmory, !!connectionResults.grimmory || (!!schemaErrors.notifications && !hasActiveGrimmory), optionalWasReviewed)
    }] : []),
    ...(showHardcover ? [{
      label: 'Hardcover',
      status: getRowStatus(isHardcoverConfigured === true, !!connectionResults.hardcover, optionalWasReviewed)
    }] : []),
    {
      label: translate('Proxy'),
      status: getRowStatus(isProxyConfigured, false, optionalWasReviewed)
    }
  ];

  return (
    <QuickstartSection
      sectionKey="quickstartReview"
      title={translate('QuickstartReviewTitle')}
      isComplete={!!interactions.quickstartReview}
    >
      <div className={styles.sectionDescription}>
        {translate('QuickstartReviewDescription')}
      </div>

      <dl className={styles.setupReviewList}>
        {rows.map((row) => {
          const rowStatus = row.status;
          const statusClassName = `setupReviewStatus${rowStatus.charAt(0).toUpperCase()}${rowStatus.slice(1)}`;

          return (
            <div
              key={row.label}
              className={styles.setupReviewRow}
            >
              <dt>{row.label}</dt>
              {row.value !== undefined && <dd>{row.value}</dd>}
              <dd
                className={`${styles.setupReviewStatus} ${styles[statusClassName]}`}
                role="status"
              >
                {getStatusLabel(rowStatus)}
              </dd>
            </div>
          );
        })}
      </dl>

      {isGuidedMode && (
        <Button
          kind={kinds.PRIMARY}
          onPress={onReviewComplete}
        >
          {translate('QuickstartFinishReview')}
        </Button>
      )}
    </QuickstartSection>
  );
}

QuickstartReviewSection.propTypes = {
  mediaScope: PropTypes.oneOf(['audiobook', 'ebook', 'both']),
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  indexers: PropTypes.arrayOf(PropTypes.object).isRequired,
  downloadClients: PropTypes.arrayOf(PropTypes.object).isRequired,
  showAudioBookShelf: PropTypes.bool.isRequired,
  showGrimmory: PropTypes.bool.isRequired,
  showHardcover: PropTypes.bool.isRequired,
  hasActiveAudioBookShelf: PropTypes.bool,
  hasActiveGrimmory: PropTypes.bool,
  isHardcoverConfigured: PropTypes.bool,
  isProxyConfigured: PropTypes.bool,
  interactions: PropTypes.object.isRequired,
  connectionResults: PropTypes.object.isRequired,
  schemaErrors: PropTypes.object.isRequired,
  isGuidedMode: PropTypes.bool.isRequired,
  onReviewComplete: PropTypes.func.isRequired
};

export default QuickstartReviewSection;
