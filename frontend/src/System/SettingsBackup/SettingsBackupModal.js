import PropTypes from 'prop-types';
import React, { useCallback, useEffect, useState } from 'react';
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
import useApiQuery from 'Store/Hooks/useApiQuery';
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

function SettingsBackupModal({ isOpen, onModalClose }) {
  const resetForm = useCallback(() => ({
    rootFolder: '/config',
    fileName: defaultFileNameUtc(),
    passphrase: '',
    passphraseConfirm: '',
    categories: getDefaultSettingsBackupCategories(),
    overwriteExistingFile: false,
    isSaving: false,
    saveError: null,
    saveResult: null
  }), []);

  const [formState, setFormState] = useState(resetForm);

  // ── Fetch backup locations via react-query (replaces manual loadLocations) ──
  const {
    data: locationsData,
    isLoading: isLoadingLocations,
    isError: locationsError
  } = useApiQuery(
    ['settingsBackup', 'locations'],
    { url: '/system/settingsbackup/locations' },
    {
      // Only fetch when the modal is open; enabled=false suspends the query.
      enabled: isOpen,
      // Locations are fetched once per modal open; staleTime keeps cached data
      // if the user closes and reopens quickly.
      staleTime: 0,
      retry: 0,
    }
  );

  const locations = Array.isArray(locationsData) ? locationsData : [];
  const firstWritable = locations.find((l) => l && l.writable && l.exists);
  const { rootFolder, fileName, passphrase, passphraseConfirm, categories, overwriteExistingFile, isSaving, saveError, saveResult } = formState;

  // Sync rootFolder from fetched locations (only when locations change or on reset)
  useEffect(() => {
    if (locations.length > 0 && rootFolder === '/config') {
      setFormState((prev) => ({
        ...prev,
        rootFolder: firstWritable ? firstWritable.path : '/config'
      }));
    }
  }, [locations, rootFolder, firstWritable]);

  const onInputChange = useCallback(({ name, value }) => {
    setFormState((prev) => ({ ...prev, [name]: value }));
  }, []);

  const onToggleCategory = useCallback((categoryKey) => {
    setFormState((prev) => ({
      categories: {
        ...prev.categories,
        [categoryKey]: !prev.categories[categoryKey]
      }
    }));
  }, []);

  const onSelectAllCategories = useCallback(() => {
    setFormState((prev) => ({
      ...prev,
      categories: getDefaultSettingsBackupCategories()
    }));
  }, []);

  const onSelectNoCategories = useCallback(() => {
    setFormState((prev) => ({
      ...prev,
      categories: {}
    }));
  }, []);

  const onSavePress = useCallback(() => {
    const selected = toCategoryList(categories);
    if (!selected.length) {
      setFormState((prev) => ({ ...prev, saveError: translate('SettingsBackupSelectAtLeastOneCategory') }));
      return;
    }

    if (!passphrase.trim()) {
      setFormState((prev) => ({ ...prev, saveError: translate('SettingsBackupPassphraseRequired') }));
      return;
    }

    if (passphrase !== passphraseConfirm) {
      setFormState((prev) => ({ ...prev, saveError: translate('SettingsBackupPassphrasesDoNotMatch') }));
      return;
    }

    setFormState((prev) => ({ ...prev, isSaving: true, saveError: null, saveResult: null }));

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

    Promise.resolve(request.request).then((data) => {
      setFormState((prev) => ({
        ...prev,
        isSaving: false,
        saveResult: data || { path: translate('SettingsBackupCreated') }
      }));
    }).catch((xhr) => {
      let errorMessage = translate('SettingsBackupFailedToCreate');
      if (xhr?.responseJSON?.message) {
        errorMessage = xhr.responseJSON.message;
      }
      setFormState((prev) => ({ ...prev, isSaving: false, saveError: errorMessage }));
    });
  }, [rootFolder, fileName, passphrase, passphraseConfirm, categories, overwriteExistingFile]);

  const handleModalClose = useCallback(() => {
    setFormState(resetForm());
    onModalClose();
  }, [onModalClose, resetForm]);

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
      <ModalContent onModalClose={handleModalClose}>
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
                onChange={onInputChange}
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
                onChange={onInputChange}
                helpText={translate('SettingsBackupFileNameHelpText')}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('SettingsBackupPassphrase')}</FormLabel>
              <FormInputGroup
                type={inputTypes.PASSWORD}
                name="passphrase"
                value={passphrase}
                onChange={onInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('SettingsBackupConfirmPassphrase')}</FormLabel>
              <FormInputGroup
                type={inputTypes.PASSWORD}
                name="passphraseConfirm"
                value={passphraseConfirm}
                onChange={onInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('Categories')}</FormLabel>
              <SettingsBackupCategoryPicker
                categories={categories}
                onToggleCategory={onToggleCategory}
                onSelectAll={onSelectAllCategories}
                onSelectNone={onSelectNoCategories}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('SettingsBackupOverwriteExistingFile')}</FormLabel>
              <FormInputGroup
                type={inputTypes.CHECK}
                name="overwriteExistingFile"
                value={overwriteExistingFile}
                onChange={() => setFormState((prev) => ({ ...prev, overwriteExistingFile: !prev.overwriteExistingFile }))}
                helpText={translate('SettingsBackupOverwriteExistingFileHelpText')}
              />
            </FormGroup>

            {isLoadingLocations && (
              <FormGroup>
                <LoadingIndicator size={20} />
              </FormGroup>
            )}

            {locationsError && (
              <FormGroup>
                <Alert
                  className={styles.statusAlert}
                  kind={kinds.DANGER}
                >
                  {translate('SettingsBackupFailedToLoadLocations', { message: 'Network error' })}
                </Alert>
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
            onPress={handleModalClose}
            kind={kinds.DEFAULT}
          >
            {translate('Close')}
          </Button>

          <SpinnerButton
            kind={kinds.PRIMARY}
            onPress={onSavePress}
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

SettingsBackupModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SettingsBackupModal;
