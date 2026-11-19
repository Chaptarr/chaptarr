import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './RestoreBackupModalContent.css';

function getErrorMessage(error) {
  if (!error || !error.responseJSON || !error.responseJSON.message) {
    return translate('BackupRestoreRequestFailed');
  }

  return error.responseJSON.message;
}

function getStepIconProps(isExecuting, hasExecuted, error) {
  if (isExecuting) {
    return {
      name: icons.SPINNER,
      isSpinning: true
    };
  }

  if (hasExecuted) {
    return {
      name: icons.CHECK,
      kind: kinds.SUCCESS
    };
  }

  if (error) {
    return {
      name: icons.FATAL,
      kind: kinds.DANGER,
      title: getErrorMessage(error)
    };
  }

  return {
    name: icons.PENDING
  };
}

class RestoreBackupModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      file: null,
      path: '',
      isRestored: false,
      isRestarted: false,
      isReloading: false
    };
  }

  componentDidUpdate(prevProps) {
    const {
      isRestoring,
      restoreError,
      isRestarting
    } = this.props;

    if (prevProps.isRestoring && !isRestoring && !restoreError) {
      this.setState({ isRestored: true });
    }

    if (prevProps.isRestarting && !isRestarting) {
      this.setState({
        isRestarted: true,
        isReloading: true
      }, () => {
        location.reload();
      });
    }
  }

  //
  // Listeners

  onPathChange = ({ value, files }) => {
    this.setState({
      file: files[0],
      path: value
    });
  };

  onRestorePress = () => {
    const {
      id,
      onRestorePress
    } = this.props;

    onRestorePress({
      id,
      file: this.state.file
    });
  };

  //
  // Render

  render() {
    const {
      id,
      name,
      isRestoring,
      restoreError,
      isRestarting,
      onModalClose,
      omittedComponents = []
    } = this.props;

    const {
      path,
      isRestored,
      isRestarted,
      isReloading
    } = this.state;

    const isRestoreDisabled = (
      (!id && !path) ||
      isRestoring ||
      isRestored ||
      isRestarting ||
      isReloading
    );

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('RestoreBackup')}
        </ModalHeader>

        <ModalBody>
          {
            !!id && translate('WouldYouLikeToRestoreBackup', { name })
          }

          {
            !id &&
              <TextInput
                type="file"
                name="path"
                value={path}
                onChange={this.onPathChange}
              />
          }

          {restoreError && <Alert kind={kinds.DANGER}>{getErrorMessage(restoreError)}</Alert>}
          {!id && <Alert kind={kinds.INFO}>{translate('BackupUploadLimitHelp')}</Alert>}
          <Alert kind={kinds.WARNING}>{translate('RestoreBackupReplaceWarning')}</Alert>
          <Alert kind={kinds.WARNING}>{translate('RestoreBackupQueueHelp')}</Alert>
          {omittedComponents.length > 0 && <Alert kind={kinds.WARNING}>{translate('BackupOmittedComponents', { components: omittedComponents.join(', ') })}</Alert>}

          <div className={styles.steps}>
            <div className={styles.step}>
              <div className={styles.stepState}>
                <Icon
                  size={20}
                  {...getStepIconProps(isRestoring, isRestored, restoreError)}
                />
              </div>

              <div>
                {translate('Restore')}
              </div>
            </div>

            <div className={styles.step}>
              <div className={styles.stepState}>
                <Icon
                  size={20}
                  {...getStepIconProps(isRestarting, isRestarted)}
                />
              </div>

              <div>
                {translate('Restart')}
              </div>
            </div>

            <div className={styles.step}>
              <div className={styles.stepState}>
                <Icon
                  size={20}
                  {...getStepIconProps(isReloading, false)}
                />
              </div>

              <div>
                {translate('Reload')}
              </div>
            </div>
          </div>
          <div className={styles.additionalInfo}>
            {translate('RestartReloadNote')}
          </div>
        </ModalBody>

        <ModalFooter>

          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>

          <SpinnerButton
            kind={kinds.WARNING}
            isDisabled={isRestoreDisabled}
            isSpinning={isRestoring}
            onPress={this.onRestorePress}
          >
            {translate('Restore')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    );
  }
}

RestoreBackupModalContent.propTypes = {
  id: PropTypes.number,
  name: PropTypes.string,
  path: PropTypes.string,
  isRestoring: PropTypes.bool.isRequired,
  restoreError: PropTypes.object,
  isRestarting: PropTypes.bool.isRequired,
  onRestorePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  omittedComponents: PropTypes.arrayOf(PropTypes.string)
};

export default RestoreBackupModalContent;
