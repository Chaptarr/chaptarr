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

const restoreModeOptions = [
  { key: 'overwrite', get value() {
    return translate('SettingsRestoreModeOverwrite');
  } },
  { key: 'merge', get value() {
    return translate('SettingsRestoreModeMerge');
  } }
];

class SettingsRestoreModal extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      locations: [],
      rootFolder: '/config',
      files: [],
      selectedFilePath: '',
      manualFilePath: '',
      passphrase: '',
      mode: 'overwrite',
      categories: getDefaultSettingsBackupCategories(),
      isLoadingLocations: false,
      isLoadingFiles: false,
      isRestoring: false,
      restoreError: null,
      restoreResult: null
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
      }, this.loadFiles);
    });

    request.request.fail(() => {
      this.setState({ isLoadingLocations: false });
    });
  };

  loadFiles = () => {
    const { rootFolder } = this.state;
    if (!rootFolder) {
      return;
    }

    this.setState({ isLoadingFiles: true, files: [], selectedFilePath: '' });

    const request = createAjaxRequest({
      url: '/system/settingsbackup/files',
      method: 'GET',
      dataType: 'json',
      data: { rootFolder }
    });

    request.request.done((data) => {
      const files = Array.isArray(data) ? data : [];
      const selectedFilePath = files.length ? files[0].path : '';

      this.setState({
        files,
        selectedFilePath,
        isLoadingFiles: false
      });
    });

    request.request.fail(() => {
      this.setState({ isLoadingFiles: false });
    });
  };

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value }, () => {
      if (name === 'rootFolder') {
        this.loadFiles();
      }
    });
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

  onRestorePress = () => {
    const {
      selectedFilePath,
      manualFilePath,
      passphrase,
      categories,
      mode
    } = this.state;

    const filePath = (manualFilePath || selectedFilePath || '').trim();
    if (!filePath) {
      this.setState({ restoreError: translate('SettingsRestoreSelectFileOrPath') });
      return;
    }

    const selected = toCategoryList(categories);
    if (!selected.length) {
      this.setState({ restoreError: translate('SettingsBackupSelectAtLeastOneCategory') });
      return;
    }

    if (!passphrase.trim()) {
      this.setState({ restoreError: translate('SettingsBackupPassphraseRequired') });
      return;
    }

    this.setState({ isRestoring: true, restoreError: null, restoreResult: null });

    const request = createAjaxRequest({
      url: '/system/settingsbackup/restore',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        filePath,
        passphrase,
        categories: selected,
        mode
      })
    });

    request.request.done((data) => {
      this.setState({
        isRestoring: false,
        restoreResult: data || { applied: [translate('SettingsRestoreDefaultApplied')] }
      }, () => {
        // Settings restore can change large parts of app state (profiles, indexers, metadata server URL, etc.).
        // Refresh the UI so the Quickstart page doesn't look stale/failed after a successful restore.
        location.reload();
      });
    });

    request.request.fail((xhr) => {
      let errorMessage = translate('SettingsRestoreFailed');
      if (xhr?.responseJSON?.message) {
        errorMessage = xhr.responseJSON.message;
      }
      this.setState({ isRestoring: false, restoreError: errorMessage });
    });
  };

  onModalClose = () => {
    this.setState({
      rootFolder: '/config',
      files: [],
      selectedFilePath: '',
      manualFilePath: '',
      passphrase: '',
      mode: 'overwrite',
      categories: getDefaultSettingsBackupCategories(),
      isRestoring: false,
      restoreError: null,
      restoreResult: null
    });

    this.props.onModalClose();
  };

  render() {
    const { isOpen } = this.props;

    const {
      locations,
      rootFolder,
      files,
      selectedFilePath,
      manualFilePath,
      passphrase,
      mode,
      categories,
      isLoadingLocations,
      isLoadingFiles,
      isRestoring,
      restoreError,
      restoreResult
    } = this.state;

    const locationOptions = locations.map((l) => ({
      key: l.path,
      value: l.path
    }));

    const fileOptions = files.map((f) => ({
      key: f.path,
      value: translate('SettingsRestoreFileOption', { name: f.name, kb: Math.round((f.size || 0) / 1024) })
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
            {translate('SettingsRestoreMySettings')}
          </ModalHeader>

          <ModalBody>
            <Form>
              <Alert
                className={styles.intro}
                kind={kinds.WARNING}
              >
                {translate('SettingsRestoreScopeWarning')}
              </Alert>

              <FormGroup>
                <FormLabel>{translate('SettingsRestoreBackupLocation')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="rootFolder"
                  values={locationOptions}
                  value={rootFolder}
                  onChange={this.onInputChange}
                  helpText={selectedWarning || translate('SettingsRestoreBackupLocationHelpText')}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsRestoreBackupFile')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="selectedFilePath"
                  values={fileOptions}
                  value={selectedFilePath}
                  onChange={this.onInputChange}
                  helpText={translate('SettingsRestoreBackupFileHelpText')}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsRestoreManualFilePath')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="manualFilePath"
                  value={manualFilePath}
                  placeholder="/downloads/chaptarr_settings_20260108_120000.chaptarr-settings-backup.json"
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('SettingsRestoreRestoreMode')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="mode"
                  values={restoreModeOptions}
                  value={mode}
                  onChange={this.onInputChange}
                  helpText={translate('SettingsRestoreModeHelpText')}
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
                <FormLabel>{translate('Categories')}</FormLabel>
                <SettingsBackupCategoryPicker
                  categories={categories}
                  onToggleCategory={this.onToggleCategory}
                  onSelectAll={this.onSelectAllCategories}
                  onSelectNone={this.onSelectNoCategories}
                />
              </FormGroup>

              {(isLoadingLocations || isLoadingFiles) && (
                <FormGroup>
                  <LoadingIndicator size={20} />
                </FormGroup>
              )}

              {restoreError && (
                <FormGroup>
                  <Alert
                    className={styles.statusAlert}
                    kind={kinds.DANGER}
                  >
                    {restoreError}
                  </Alert>
                </FormGroup>
              )}

              {restoreResult && (
                <FormGroup>
                  <Alert
                    className={styles.statusAlert}
                    kind={kinds.SUCCESS}
                  >
                    <div><strong>{translate('SettingsRestoreAppliedLabel')}</strong> {(restoreResult.applied || []).join(', ')}</div>
                    {(restoreResult.warnings || []).length ? (
                      <div className={styles.restoreWarnings}>
                        <strong>{translate('SettingsRestoreWarningsLabel')}</strong>
                        <ul>
                          {restoreResult.warnings.map((w) => <li key={w}>{w}</li>)}
                        </ul>
                      </div>
                    ) : null}
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
              onPress={this.onRestorePress}
              isDisabled={isRestoring}
              isSpinning={isRestoring}
            >
              {translate('Restore')}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

SettingsRestoreModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SettingsRestoreModal;
