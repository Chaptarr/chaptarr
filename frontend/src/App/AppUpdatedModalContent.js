import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import InlineMarkdown from 'Components/Markdown/InlineMarkdown';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import UpdateChanges from 'System/Updates/UpdateChanges';
import translate from 'Utilities/String/translate';
import styles from './AppUpdatedModalContent.css';

function AppUpdatedModalContent(props) {
  const {
    version,
    isPopulated,
    error,
    items,
    onSeeChangesPress,
    onModalClose
  } = props;

  // Unlike the cumulative sibling-*arr popup, show only the installed release.
  // The update API supplies its in-binary notes when the feed has no installed row.
  // The manifest may omit zero version components that the running assembly includes.
  const releaseVersion = (value) => value.replace(/(?:\.0)+$/, '');
  const update = items.find((item) => releaseVersion(item.version) === releaseVersion(version));

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {translate('AppUpdated', { appName: 'Chaptarr' })}
      </ModalHeader>

      <ModalBody>
        <div>
          <InlineMarkdown data={translate('AppUpdatedVersion', { appName: 'Chaptarr', version })} blockClassName={styles.version} />
        </div>

        {
          isPopulated && !error && !!update &&
            <div>
              {
                !update.changes &&
                  <div className={styles.maintenance}>{translate('MaintenanceRelease')}</div>
              }

              {
                !!update.changes &&
                  <div>
                    <div className={styles.changes}>
                      {translate('WhatsNew')}
                    </div>

                    <UpdateChanges
                      title={translate('New')}
                      changes={update.changes.new}
                    />

                    <UpdateChanges
                      title={translate('Fixed')}
                      changes={update.changes.fixed}
                    />
                  </div>
              }
            </div>
        }

        {
          !!error &&
            <Alert kind={kinds.WARNING}>
              {translate('FailedToFetchUpdates')}
            </Alert>
        }

        {
          !isPopulated && !error &&
            <LoadingIndicator />
        }
      </ModalBody>

      <ModalFooter>
        <Button
          onPress={onSeeChangesPress}
        >
          {translate('RecentChanges')}
        </Button>

        <Button
          kind={kinds.PRIMARY}
          onPress={onModalClose}
        >
          {translate('Reload')}
        </Button>
      </ModalFooter>
    </ModalContent>
  );
}

AppUpdatedModalContent.propTypes = {
  version: PropTypes.string.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onSeeChangesPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default AppUpdatedModalContent;
