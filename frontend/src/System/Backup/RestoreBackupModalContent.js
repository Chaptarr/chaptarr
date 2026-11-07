import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormLabel from 'Components/Form/FormLabel';
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
import translateServerMessage from 'Utilities/String/translateServerMessage';
import styles from './RestoreBackupModalContent.css';

function getErrorMessage(error) {
  if (!error || !error.responseJSON || !error.responseJSON.message) {
    return translate('BackupRestoreError');
  }

  return translateServerMessage(error.responseJSON.message);
}

function isBookshelfEncryptedBackup(file) {
  const filename = file && file.name ? file.name.toLowerCase() : '';
  return filename.endsWith('.enc') && !filename.endsWith('.zip.enc');
}

function isValidPassphrase(passphrase) {
  return passphrase.length >= 16 && new TextEncoder().encode(passphrase).length <= 4096;
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
      kinds: kinds.DANGER,
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
      passphrase: '',
      isRestored: false,
      isRestarted: false,
      isReloading: false
    };
  }

  componentDidUpdate(prevProps) {
    const {
      isRestoring,
      restoreError,
      isRestarting,
      dispatchRestart
    } = this.props;

    if (prevProps.isRestoring && !isRestoring && !restoreError) {
      this.setState({ isRestored: true }, () => {
        dispatchRestart();
      });
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
      path: value,
      passphrase: ''
    });
  };

  onPassphraseChange = ({ value }) => {
    this.setState({
      passphrase: value
    });
  };

  onModalClose = () => {
    this.setState({
      file: null,
      path: '',
      passphrase: ''
    });
    this.props.onModalClose();
  };

  onRestorePress = () => {
    const {
      id,
      onRestorePress
    } = this.props;

    const {
      file,
      passphrase
    } = this.state;

    onRestorePress({
      id,
      file,
      passphrase
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
      isRestarting
    } = this.props;

    const {
      path,
      file,
      passphrase,
      isRestored,
      isRestarted,
      isReloading
    } = this.state;

    const isRestoreDisabled = (
      (!id && !path) ||
      (isBookshelfEncryptedBackup(file) && !isValidPassphrase(passphrase)) ||
      isRestoring ||
      isRestarting ||
      isReloading
    );

    return (
      <ModalContent onModalClose={this.onModalClose}>
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

          {
            isBookshelfEncryptedBackup(file) &&
              <>
                <Alert kind={kinds.INFO}>
                  {translate('EncryptedBackupRestoreHelpText')}
                </Alert>

                <FormGroup>
                  <FormLabel name="restoreBackupPassphrase">
                    {translate('SettingsBackupPassphrase')}
                  </FormLabel>
                  <TextInput
                    id="restoreBackupPassphrase"
                    name="passphrase"
                    type="password"
                    autoComplete="current-password"
                    maxLength={4096}
                    value={passphrase}
                    onChange={this.onPassphraseChange}
                  />
                </FormGroup>
              </>
          }

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
        </ModalBody>

        <ModalFooter>
          <div className={styles.additionalInfo}>
            {translate('RestartReloadNote')}
          </div>

          <Button onPress={this.onModalClose}>
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
  dispatchRestart: PropTypes.func.isRequired,
  onRestorePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default RestoreBackupModalContent;
