import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import SettingsBackupCategoryPicker, {
  getDefaultSettingsBackupCategories,
  toCategoryList
} from './SettingsBackupCategoryPicker';
import styles from './SettingsBackupModal.css';

function defaultFileNameUtc() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  const stamp = `${d.getUTCFullYear()}${pad(d.getUTCMonth() + 1)}${pad(d.getUTCDate())}_${pad(d.getUTCHours())}${pad(d.getUTCMinutes())}${pad(d.getUTCSeconds())}`;
  return `chaptarr_settings_${stamp}`;
}

class SettingsBackupModal extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      locations: [],
      rootFolder: '/config',
      fileName: defaultFileNameUtc(),
      passphrase: '',
      passphraseConfirm: '',
      categories: getDefaultSettingsBackupCategories(),
      overwriteExistingFile: false,
      isLoadingLocations: false,
      isSaving: false,
      saveError: null,
      saveResult: null
    };
  }

  componentDidUpdate(prevProps) {
    if (!prevProps.isOpen && this.props.isOpen) {
      this.loadLocations();
    }
  }

  loadLocations = () => {
    this.setState({ isLoadingLocations: true });

    const request = createAjaxRequest({
      url: '/system/settingsbackup/locations',
      method: 'GET',
      dataType: 'json'
    });

    request.request.done((data) => {
      const locations = Array.isArray(data) ? data : [];
      const firstWritable = locations.find((l) => l && l.writable && l.exists);
      const rootFolder = firstWritable ? firstWritable.path : '/config';

      this.setState({
        locations,
        rootFolder,
        isLoadingLocations: false
      });
    });

    request.request.fail(() => {
      this.setState({ isLoadingLocations: false });
    });
  };

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });
  };

  onToggleCategory = (categoryKey) => {
    this.setState((state) => ({
      categories: {
        ...state.categories,
        [categoryKey]: !state.categories[categoryKey]
      }
    }));
  };

  onSelectAllCategories = () => {
    this.setState({ categories: getDefaultSettingsBackupCategories() });
  };

  onSelectNoCategories = () => {
    this.setState({ categories: {} });
  };

  onSavePress = () => {
    const {
      rootFolder,
      fileName,
      passphrase,
      passphraseConfirm,
      categories,
      overwriteExistingFile
    } = this.state;

    const selected = toCategoryList(categories);
    if (!selected.length) {
      this.setState({ saveError: translate('SettingsBackupSelectAtLeastOneCategory') });
      return;
    }

    if (!passphrase.trim()) {
      this.setState({ saveError: translate('SettingsBackupPassphraseRequired') });
      return;
    }

    if (passphrase !== passphraseConfirm) {
      this.setState({ saveError: translate('SettingsBackupPassphrasesDoNotMatch') });
      return;
    }

    this.setState({ isSaving: true, saveError: null, saveResult: null });

    const request = createAjaxRequest({
      url: '/system/settingsbackup/create',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        rootFolder,
        fileName,
        passphrase,
        categories: selected,
        overwriteExistingFile
      })
    });

    request.request.done((data) => {
      this.setState({
        isSaving: false,
        saveResult: data || { path: translate('SettingsBackupCreated') }
      });
    });

    request.request.fail((xhr) => {
      let errorMessage = translate('SettingsBackupFailedToCreate');
      if (xhr?.responseJSON?.message) {
        errorMessage = xhr.responseJSON.message;
      }
      this.setState({ isSaving: false, saveError: errorMessage });
    });
  };

  onModalClose = () => {
    this.setState({
      rootFolder: '/config',
      fileName: defaultFileNameUtc(),
      passphrase: '',
      passphraseConfirm: '',
      categories: getDefaultSettingsBackupCategories(),
      overwriteExistingFile: false,
      isSaving: false,
      saveError: null,
      saveResult: null
    });

    this.props.onModalClose();
  };

  render() {
    const {
      isOpen
    } = this.props;

    const {
      locations,
      rootFolder,
      fileName,
      passphrase,
      passphraseConfirm,
      categories,
      overwriteExistingFile,
      isLoadingLocations,
      isSaving,
      saveError,
      saveResult
    } = this.state;

    const locationOptions = locations.map((l) => ({
      key: l.path,
      value: l.path
    }));

    const selectedLocation = locations.find((l) => l.path === rootFolder);
    const selectedWarning = selectedLocation?.warning;

    return (
      <Modal
        isOpen={isOpen}
        size={sizes.LARGE}
      >
        <ModalContent onModalClose={this.onModalClose}>
          <ModalHeader>
            {translate('SettingsBackupMySettings')}
          </ModalHeader>

          <ModalBody>
            <Form>
              <Alert
                className={styles.intro}
                kind={kinds.WARNING}
              >
                {translate('SettingsBackupScopeWarning')}
              </Alert>

              <FormGroup>
                <FormLabel>{translate('SettingsBackupSaveLocation')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="rootFolder"
                  values={locationOptions}
                  value={rootFolder}
                  onChange={this.onInputChange}
                  helpText={selectedWarning || translate('SettingsBackupSaveLocationHelpText')}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsBackupFileName')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="fileName"
                  value={fileName}
                  placeholder="chaptarr_settings_YYYYMMDD_HHMMSS"
                  onChange={this.onInputChange}
                  helpText={translate('SettingsBackupFileNameHelpText')}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsBackupPassphrase')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="passphrase"
                  value={passphrase}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsBackupConfirmPassphrase')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="passphraseConfirm"
                  value={passphraseConfirm}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('Categories')}</FormLabel>
                <SettingsBackupCategoryPicker
                  categories={categories}
                  onToggleCategory={this.onToggleCategory}
                  onSelectAll={this.onSelectAllCategories}
                  onSelectNone={this.onSelectNoCategories}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsBackupOverwriteExistingFile')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="overwriteExistingFile"
                  value={overwriteExistingFile}
                  onChange={() => this.setState({ overwriteExistingFile: !overwriteExistingFile })}
                  helpText={translate('SettingsBackupOverwriteExistingFileHelpText')}
                />
              </FormGroup>

              {isLoadingLocations && (
                <FormGroup>
                  <LoadingIndicator size={20} />
                </FormGroup>
              )}

              {saveError && (
                <FormGroup>
                  <Alert
                    className={styles.statusAlert}
                    kind={kinds.DANGER}
                  >
                    {saveError}
                  </Alert>
                </FormGroup>
              )}

              {saveResult && (
                <FormGroup>
                  <Alert
                    className={styles.statusAlert}
                    kind={kinds.SUCCESS}
                  >
                    <div><strong>{translate('SettingsBackupSavedLabel')}</strong> {saveResult.path}</div>
                  </Alert>
                </FormGroup>
              )}
            </Form>
          </ModalBody>

          <ModalFooter>
            <Button
              onPress={this.onModalClose}
              kind={kinds.DEFAULT}
            >
              {translate('Close')}
            </Button>

            <SpinnerButton
              kind={kinds.PRIMARY}
              onPress={this.onSavePress}
              isDisabled={isSaving}
              isSpinning={isSaving}
            >
              {translate('Backup')}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

SettingsBackupModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SettingsBackupModal;
