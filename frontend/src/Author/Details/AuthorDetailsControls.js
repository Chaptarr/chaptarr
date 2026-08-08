import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import FilterSearchInput from './FilterSearchInput';
import MediaTypeToggle from './MediaTypeToggle';
import MonitorToggleSwitch from './MonitorToggleSwitch';
import styles from './AuthorDetailsControls.css';

function AuthorDetailsControls(props) {
  const {
    authorId,
    selectedMediaType,
    onMediaTypeChange,
    onSelectRootFolderPress,
    hasAudiobookRootFolder,
    hasEbookRootFolder,
    hasAudiobookRootFoldersAvailable,
    hasEbookRootFoldersAvailable,
    authorHasAudiobookConfig,
    authorHasEbookConfig,
    syncMonitoredAcrossFormats,
    rootFoldersPopulated,
    resizeKey
  } = props;

  // Determine what kind of root folder issue we have for the current media type
  const currentMediaTypeAvailable = selectedMediaType === 'audiobook' ? hasAudiobookRootFoldersAvailable : hasEbookRootFoldersAvailable;
  const currentMediaTypeConfigured = selectedMediaType === 'audiobook' ? authorHasAudiobookConfig : authorHasEbookConfig;

  // Show message if there's a root folder issue
  const showRootFolderMessage = !currentMediaTypeAvailable || !currentMediaTypeConfigured;

  const mediaTypeName = selectedMediaType === 'audiobook' ? 'audiobook' : 'ebook';

  // Determine the right message and CTA based on the specific situation
  let messageText = null;
  let ctaText = null;
  let ctaLink = null;
  let ctaOnPress = null;

  if (!currentMediaTypeAvailable) {
    // No compatible root folders exist globally
    messageText = `No ${mediaTypeName} root folders are configured.`;
    ctaText = 'Manage root folders';
    ctaLink = '/settings/mediamanagement';
  } else if (!currentMediaTypeConfigured) {
    // Global folders exist but author hasn't selected one
    messageText = `No ${mediaTypeName} root folder selected for this author.`;
    ctaText = 'Select root folder';
    ctaOnPress = () => onSelectRootFolderPress(selectedMediaType);
  }

  return (
    <div className={styles.controlsContainer}>
      <div className={styles.topRow}>
        <div className={styles.leftControls}>
          <div className={styles.syncAndToggle}>
            {
              syncMonitoredAcrossFormats ?
                <div
                  className={styles.syncIndicator}
                  title={translate('AudioEbookSyncTooltip')}
                >
                  <Icon
                    name={icons.REFRESH}
                    size={13}
                  />
                  <span>{translate('AudioEbookSyncIsOn')}</span>
                </div> :
                null
            }

            <MediaTypeToggle
              selectedMediaType={selectedMediaType}
              onMediaTypeChange={onMediaTypeChange}
              hasAudiobookRootFolder={hasAudiobookRootFolder}
              hasEbookRootFolder={hasEbookRootFolder}
            />
          </div>

          <FilterSearchInput authorId={authorId} />
        </div>
        <div className={styles.rightControls}>
          <MonitorToggleSwitch resizeKey={resizeKey} />
        </div>
      </div>

      {rootFoldersPopulated && showRootFolderMessage && (
        <div className={styles.rootFolderMessage}>
          <Alert
            kind={kinds.INFO}
            role="status"
            aria-live="polite"
          >
            {messageText}{' '}
            <Button
              kind={kinds.PRIMARY}
              size="small"
              to={ctaLink}
              onPress={ctaOnPress}
            >
              {ctaText}
            </Button>
          </Alert>
        </div>
      )}
    </div>
  );
}

AuthorDetailsControls.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  onSelectRootFolderPress: PropTypes.func.isRequired,
  hasAudiobookRootFolder: PropTypes.bool,
  hasEbookRootFolder: PropTypes.bool,
  hasAudiobookRootFoldersAvailable: PropTypes.bool,
  hasEbookRootFoldersAvailable: PropTypes.bool,
  authorHasAudiobookConfig: PropTypes.bool,
  authorHasEbookConfig: PropTypes.bool,
  syncMonitoredAcrossFormats: PropTypes.bool,
  rootFoldersPopulated: PropTypes.bool,
  resizeKey: PropTypes.oneOfType([PropTypes.string, PropTypes.number])
};

export default AuthorDetailsControls;
