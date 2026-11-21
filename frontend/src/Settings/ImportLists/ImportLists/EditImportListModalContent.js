import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import AuthorMonitoringGatePopoverContent from 'AddAuthor/AuthorMonitoringGatePopoverContent';
import AuthorMonitoringOptionsPopoverContent from 'AddAuthor/AuthorMonitoringOptionsPopoverContent';
import AuthorMonitorNewItemsOptionsPopoverContent from 'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import ProviderFieldFormGroup from 'Components/Form/ProviderFieldFormGroup';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Popover from 'Components/Tooltip/Popover';
import { icons, inputTypes, kinds, tooltipPositions } from 'Helpers/Props';
import { FolderType } from 'Helpers/Props/folderTypes';
import AdvancedSettingsButton from 'Settings/AdvancedSettingsButton';
import HardcoverApiKeyModal from 'System/Quickstart/HardcoverApiKeyModal';
import monitorNewItemsOptions from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions from 'Utilities/Author/monitorOptions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './EditImportListModalContent.css';

const listMonitorOptions = monitorOptions.flatMap((option) => {
  if (option.key === 'all') {
    return [option, { key: 'specificBook', get value() {
      return translate('ImportListOnlyListedBooks');
    } }];
  }
  return [option];
});

function EditImportListModalContent(props) {

  const {
    advancedSettings,
    isFetching,
    isRootFoldersPopulated,
    rootFoldersError,
    error,
    isSaving,
    isTesting,
    saveError,
    item,
    onInputChange,
    onFieldChange,
    onRetryRootFolders,
    onModalClose,
    onSavePress,
    onTestPress,
    onAdvancedSettingsPress,
    onDeleteImportListPress,
    monitoringDefaults = {},
    ...otherProps
  } = props;

  const {
    id,
    name,
    enableAutomaticAdd,
    implementation,
    implementationName,
    shouldMonitor,
    shouldMonitorExisting,
    shouldSearch,
    monitoringOptions,
    fields,
    message
  } = item;

  const options = monitoringOptions?.value || {};
  const addMissingAuthors = options.addMissingAuthors !== false;
  const changeOptions = ({ name: optionName, value }) => onInputChange({
    name: 'monitoringOptions', value: { ...options, [optionName]: value }
  });

  const renderNewAuthorMonitoring = (mediaType) => {
    const monitor = options[`${mediaType}Monitor`] ?? monitoringDefaults[`${mediaType}Monitor`] ?? 'none';
    const legacyMonitorListedBooks = ['specificBook', 'entireAuthor'].includes(shouldMonitor?.value);
    const monitorListedBooks = options[`${mediaType}MonitorListedBooks`] ?? legacyMonitorListedBooks;
    const selectedMonitor = monitorListedBooks && monitor === 'none' ? 'specificBook' : monitor;
    const changeMonitor = ({ value }) => {
      const includeListed = value === 'specificBook' || (value !== 'none' && monitorListedBooks);
      onInputChange({
        name: 'monitoringOptions',
        value: {
          ...options,
          [`${mediaType}Monitor`]: value,
          [`${mediaType}MonitorListedBooks`]: includeListed
        }
      });
    };

    return (
      <>
        <FormGroup>
          <FormLabel>
            {translate('ImportListMonitorWhenAdded')}
            <Popover
              anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
              title={translate('ImportListMonitorWhenAdded')}
              body={<AuthorMonitoringGatePopoverContent />}
              position={tooltipPositions.RIGHT}
            />
          </FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name={`${mediaType}Monitored`}
            value={options[`${mediaType}Monitored`] ?? monitoringDefaults[`${mediaType}Monitored`]}
            onChange={changeOptions}
          />
        </FormGroup>
        <FormGroup>
          <FormLabel>
            {translate('InitialBookMonitoring')}
            <Popover
              anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
              title={translate('InitialBookMonitoring')}
              body={
                <>
                  <AuthorMonitoringOptionsPopoverContent />
                  <p>{translate('ImportListBookMonitoringOptionsHelp')}</p>
                </>
              }
              position={tooltipPositions.RIGHT}
            />
          </FormLabel>
          <FormInputGroup
            type={inputTypes.SELECT}
            name={`${mediaType}Monitor`}
            values={listMonitorOptions}
            value={selectedMonitor}
            helpText={translate('ImportListInitialBooksHelp')}
            onChange={changeMonitor}
          />
        </FormGroup>
        {
          ['all', 'missing', 'existing'].includes(selectedMonitor) &&
            <FormGroup>
              <FormLabel>{translate('ImportListMonitorListedBooks')}</FormLabel>
              <FormInputGroup
                type={inputTypes.CHECK}
                name={`${mediaType}MonitorListedBooks`}
                value={monitorListedBooks}
                helpText={translate('ImportListMonitorListedBooksHelp')}
                onChange={changeOptions}
              />
            </FormGroup>
        }
        <FormGroup>
          <FormLabel>
            {translate('MonitorNewBooks')}
            <Popover
              anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
              title={translate('MonitorNewBooks')}
              body={<AuthorMonitorNewItemsOptionsPopoverContent />}
              position={tooltipPositions.RIGHT}
            />
          </FormLabel>
          <FormInputGroup
            type={inputTypes.SELECT}
            name={`${mediaType}MonitorNewItems`}
            values={monitorNewItemsOptions}
            value={options[`${mediaType}MonitorNewItems`] ?? monitoringDefaults[`${mediaType}MonitorNewItems`]}
            helpText={translate('ImportListFutureBooksHelp')}
            onChange={changeOptions}
          />
        </FormGroup>
      </>
    );
  };

  const isHardcoverLibraryImportList = (implementation?.value === 'HardcoverLibraryImportList') ||
    (implementationName === 'Hardcover Library');

  const keepListedBooksHelp = isHardcoverLibraryImportList ? 'ImportListKeepHardcoverBooksHelp' : 'ImportListKeepListedBooksHelp';

  const isGoodreadsBookshelfImportList = (implementation?.value === 'GoodreadsBookshelf') ||
    (implementationName === 'Goodreads Bookshelves');

  const [hardcoverIdentity, setHardcoverIdentity] = useState(null);
  const [hardcoverIdentityReload, setHardcoverIdentityReload] = useState(0);
  const [isHardcoverApiKeyModalOpen, setIsHardcoverApiKeyModalOpen] = useState(false);

  useEffect(() => {
    if (!isHardcoverLibraryImportList) {
      setHardcoverIdentity(null);
      return;
    }

    const request = createAjaxRequest({
      url: '/config/hardcover',
      method: 'GET'
    });

    request.request.done((data) => {
      setHardcoverIdentity({
        enabled: !!data?.enabled,
        hasToken: !!data?.hasToken,
        username: data?.username || '',
        avatarUrl: data?.avatarUrl || ''
      });
    });

    request.request.fail(() => {
      setHardcoverIdentity(null);
    });

    return () => {
      request.abortRequest();
    };
  }, [isHardcoverLibraryImportList, hardcoverIdentityReload]);

  const getSaveErrorMessage = (xhr) => {
    if (!xhr) {
      return null;
    }

    const responseJson = xhr.responseJSON;
    if (responseJson) {
      // FluentValidation failures come back as an array; surface messages instead of a generic "400 Bad Request".
      if (Array.isArray(responseJson)) {
        const messages = responseJson
          .map((f) => {
            if (typeof f === 'string') {
              return f;
            }

            return f?.errorMessage || f?.message || null;
          })
          .filter((v) => typeof v === 'string' && v.trim().length);

        if (messages.length) {
          return messages.join(' ');
        }
      }

      if (typeof responseJson === 'string') {
        return responseJson;
      }

      if (responseJson.message) {
        return responseJson.message;
      }

      if (responseJson.error) {
        return responseJson.error;
      }

      if (responseJson.detail) {
        return responseJson.detail;
      }

      if (responseJson.errors && typeof responseJson.errors === 'object') {
        const errorValues = Object.values(responseJson.errors)
          .flat()
          .filter((v) => typeof v === 'string' && v.trim().length);

        if (errorValues.length) {
          return errorValues.join(' ');
        }
      }
    }

    if (xhr.responseText) {
      try {
        const parsed = JSON.parse(xhr.responseText);
        if (parsed?.message) {
          return parsed.message;
        }
      } catch (e) {
        // Ignore JSON parse errors.
      }
    }

    if (xhr.status && xhr.statusText) {
      return `${xhr.status} ${xhr.statusText}`;
    }

    return xhr.statusText || 'An unknown error occurred';
  };

  const renderProviderField = (field) => {
    return (
      <ProviderFieldFormGroup
        key={field.name}
        advancedSettings={advancedSettings}
        provider="importList"
        providerData={item}
        section="settings.importLists"
        {...field}
        onChange={onFieldChange}
      />
    );
  };

  const getField = (fieldName) => fields?.find((f) => f.name === fieldName);

  const getDualMediaSelectedMediaType = () => {
    const includesAudiobooks = !!getField('monitorAudiobooks')?.value;
    const includesEbooks = !!getField('monitorEbooks')?.value;

    if (includesAudiobooks && includesEbooks) {
      return 'both';
    }

    if (includesEbooks) {
      return 'ebook';
    }

    return includesAudiobooks ? 'audiobook' : '';
  };

  const dualMediaSelectedMediaType = getDualMediaSelectedMediaType();
  const dualMediaAudiobookRootFolderPath = getField('audiobookRootFolderPath')?.value;
  const dualMediaEbookRootFolderPath = getField('ebookRootFolderPath')?.value;
  const dualMediaSaveNeedsAudiobooks = dualMediaSelectedMediaType === 'audiobook' || dualMediaSelectedMediaType === 'both';
  const dualMediaSaveNeedsEbooks = dualMediaSelectedMediaType === 'ebook' || dualMediaSelectedMediaType === 'both';
  const goodreadsUserId = isGoodreadsBookshelfImportList ? (getField('userId')?.value || '').trim() : '';
  const goodreadsBookshelfIdsRaw = isGoodreadsBookshelfImportList ? getField('bookshelfIds')?.value : [];
  const goodreadsBookshelfIds = Array.isArray(goodreadsBookshelfIdsRaw) ? goodreadsBookshelfIdsRaw : [];
  const isGoodreadsMissingUserId = isGoodreadsBookshelfImportList && !goodreadsUserId;
  const isGoodreadsMissingBookshelves = isGoodreadsBookshelfImportList && goodreadsBookshelfIds.length === 0;
  const isGoodreadsValidationDisabled = isGoodreadsMissingUserId || isGoodreadsMissingBookshelves;

  const isSaveDisabled =
    (addMissingAuthors && !isRootFoldersPopulated) ||
    (addMissingAuthors &&
      ((dualMediaSaveNeedsAudiobooks && !dualMediaAudiobookRootFolderPath) ||
        (dualMediaSaveNeedsEbooks && !dualMediaEbookRootFolderPath))) ||
    isGoodreadsValidationDisabled || !dualMediaSelectedMediaType;

  const goodreadsValidationMessage = (() => {
    if (!isGoodreadsBookshelfImportList || !isGoodreadsValidationDisabled) {
      return null;
    }

    const messages = [];

    if (isGoodreadsMissingUserId) {
      messages.push(translate('GoodreadsImportListUserIdRequired'));
    }

    if (isGoodreadsMissingBookshelves) {
      messages.push(translate('GoodreadsImportListBookshelfRequired'));
    }

    return messages.join(' ');
  })();

  const saveLabel = (() => {
    if (id || !dualMediaSelectedMediaType) {
      return translate('Save');
    }

    if (dualMediaSelectedMediaType === 'ebook') {
      return translate('AddListEbooks');
    }

    if (dualMediaSelectedMediaType === 'both') {
      return translate('AddListAudiobooksAndEbooks');
    }

    return translate('AddListAudiobooks');
  })();

  const mediaFieldNames = new Set([
    'monitorAudiobooks',
    'monitorEbooks',
    'audiobookQualityProfileId',
    'ebookQualityProfileId',
    'audiobookMetadataProfileId',
    'ebookMetadataProfileId',
    'audiobookRootFolderPath',
    'ebookRootFolderPath',
    'audiobookTags',
    'ebookTags'
  ]);

  const providerSpecificFields = (fields || []).filter((field) => !mediaFieldNames.has(field.name));

  const onMediaTypeChange = (mediaType) => {
    onFieldChange({ name: 'monitorAudiobooks', value: mediaType !== 'ebook' });
    onFieldChange({ name: 'monitorEbooks', value: mediaType !== 'audiobook' });
  };

  const renderDualMediaImportListFields = () => {
    const audiobookRootFolderField = getField('audiobookRootFolderPath');
    const ebookRootFolderField = getField('ebookRootFolderPath');

    const renderRootFolderField = (field, folderType) => {
      if (!field) {
        return null;
      }

      if (
        field.hidden === 'hidden' ||
        (field.hidden === 'hiddenIfNotSet' && !field.value)
      ) {
        return null;
      }

      return (
        <FormGroup
          key={field.name}
          advancedSettings={advancedSettings}
          isAdvanced={field.advanced}
        >
          <FormLabel>{field.label}</FormLabel>
          <FormInputGroup
            type={inputTypes.ROOT_FOLDER_SELECT}
            name={field.name}
            helpText={field.helpText}
            helpTextWarning={field.helpTextWarning}
            helpLink={field.helpLink}
            value={field.value}
            errors={field.errors}
            warnings={field.warnings}
            pending={field.pending}
            includeMissingValue={true}
            folderType={folderType}
            onChange={onFieldChange}
          />
        </FormGroup>
      );
    };

    const renderAudiobookFields = () => {
      if (!addMissingAuthors) {
        return null;
      }

      return (
        <div className={styles.hardcoverSubsection}>
          {renderRootFolderField(audiobookRootFolderField, FolderType.Audiobook)}
          {renderNewAuthorMonitoring('audiobook')}
          {fields.filter((f) => f.name === 'audiobookQualityProfileId' || f.name === 'audiobookMetadataProfileId' || f.name === 'audiobookTags').map(renderProviderField)}
        </div>
      );
    };

    const renderEbookFields = () => {
      if (!addMissingAuthors) {
        return null;
      }

      return (
        <div className={styles.hardcoverSubsection}>
          {renderRootFolderField(ebookRootFolderField, FolderType.Ebook)}
          {renderNewAuthorMonitoring('ebook')}
          {fields.filter((f) => f.name === 'ebookQualityProfileId' || f.name === 'ebookMetadataProfileId' || f.name === 'ebookTags').map(renderProviderField)}
        </div>
      );
    };

    return (
      <>
        {dualMediaSelectedMediaType === 'audiobook' ? renderAudiobookFields() : null}
        {dualMediaSelectedMediaType === 'ebook' ? renderEbookFields() : null}
        {dualMediaSelectedMediaType === 'both' ? (
          <>
            {renderAudiobookFields()}
            {renderEbookFields()}
          </>
        ) : null}
      </>
    );
  };

  const renderHardcoverIntro = () => {
    if (!isHardcoverLibraryImportList) {
      return null;
    }

    const isConnected = hardcoverIdentity?.hasToken === true;
    const isMissingToken = hardcoverIdentity?.hasToken === false;
    const hasUsername = !!hardcoverIdentity?.username;
    let alertKind = kinds.INFO;
    if (!isConnected && isMissingToken) {
      alertKind = kinds.WARNING;
    }

    return (
      <>
        <Alert
          kind={alertKind}
          className={styles.hardcoverInfo}
        >
          {
            isConnected && hasUsername ?
              <div className={styles.hardcoverIdentityRow}>
                {hardcoverIdentity.avatarUrl && (
                  <img
                    className={styles.hardcoverIdentityAvatar}
                    src={hardcoverIdentity.avatarUrl}
                    alt=""
                  />
                )}
                <div className={styles.hardcoverIdentityText}>
                  <strong>{hardcoverIdentity.username}</strong>
                </div>
              </div> :
              null
          }

          {isMissingToken ? (
            <div>
              {translate('ImportListHardcoverDisabled')}
            </div>
          ) : null}

          <div>{translate('ImportListHardcoverFormatsHelp')}</div>
          <div>{translate('ImportListHardcoverNarrationHelp')}</div>

          {isMissingToken ? (
            <div className={styles.hardcoverConnectActions}>
              <Button
                kind={kinds.PRIMARY}
                onPress={() => setIsHardcoverApiKeyModalOpen(true)}
              >
                {translate('ConnectHardcover')}
              </Button>
            </div>
          ) : null}
        </Alert>

        <HardcoverApiKeyModal
          isOpen={isHardcoverApiKeyModalOpen}
          onModalClose={() => {
            setIsHardcoverApiKeyModalOpen(false);
            setHardcoverIdentityReload((x) => x + 1);
          }}
        />
      </>
    );
  };

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {id ? translate('EditList') : translate('AddList')}
      </ModalHeader>

      <ModalBody>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <div>
              {translate('UnableToAddANewListPleaseTryAgain')}
            </div>
        }

        {
          !isFetching && !error &&
            <Form {...otherProps}>
              {
                !!message &&
                  <Alert
                    className={styles.message}
                    kind={message.value.type}
                  >
                    {message.value.message}
                  </Alert>
              }

              {
                !!saveError &&
                  <Alert
                    className={styles.message}
                    kind={kinds.DANGER}
                    role="alert"
                  >
                    {getSaveErrorMessage(saveError)}
                  </Alert>
              }

              {
                (!!goodreadsValidationMessage || !dualMediaSelectedMediaType) &&
                  <Alert
                    className={styles.message}
                    kind={kinds.WARNING}
                    role="alert"
                  >
                    {[!dualMediaSelectedMediaType && translate('AuthorDeleteSelectFormat'), goodreadsValidationMessage].filter(Boolean).join(' ')}
                  </Alert>
              }

              {renderHardcoverIntro()}

              <FieldSet legend={translate('ImportListSettings')}>
                <FormGroup>
                  <FormLabel>
                    {translate('Name')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="name"
                    {...name}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('ImportListEnabled')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="enableAutomaticAdd"
                    helpText={translate('ImportListEnabledHelp')}
                    {...enableAutomaticAdd}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>{translate('SearchForNewItems')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="shouldSearch"
                    {...shouldSearch}
                    helpText={translate('ImportListSearchHelp')}
                    onChange={onInputChange}
                  />
                </FormGroup>
                <MediaTypeToggle
                  className={styles.hardcoverMediaTypeToggle}
                  selectedMediaType={dualMediaSelectedMediaType}
                  onMediaTypeChange={onMediaTypeChange}
                  includeBoth={true}
                />
                <Alert kind={kinds.INFO}>{translate('ImportListRemovalHelp')}</Alert>
              </FieldSet>

              <FieldSet legend={translate('ImportListExistingAuthorSettings')}>
                <FormGroup>
                  <FormLabel>{translate('ImportListKeepListedBooks')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="shouldMonitorExisting"
                    {...shouldMonitorExisting}
                    helpText={translate(shouldMonitorExisting?.value ?
                      keepListedBooksHelp :
                      'ImportListKeepListedBooksOffHelp')}
                    onChange={onInputChange}
                  />
                </FormGroup>
                <FormGroup>
                  <FormLabel>{translate('ImportListResumeAuthors')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="resumePausedAuthors"
                    value={options.resumePausedAuthors === true}
                    helpText={options.resumePausedAuthors === true ? undefined : translate('ImportListResumeAuthorsHelp')}
                    onChange={changeOptions}
                  />
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('ImportListNewAuthorSettings')}>
                <FormGroup>
                  <FormLabel>{translate('ImportListAddMissingAuthors')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="addMissingAuthors"
                    value={addMissingAuthors}
                    helpText={translate('ImportListAddMissingAuthorsHelp')}
                    onChange={changeOptions}
                  />
                </FormGroup>
                {
                  addMissingAuthors &&
                    <Alert kind={kinds.INFO}>{translate('ImportListRootDefaultsHelp')}</Alert>
                }
                {addMissingAuthors && !isRootFoldersPopulated && !rootFoldersError && <LoadingIndicator />}
                {addMissingAuthors && !isRootFoldersPopulated && rootFoldersError && (
                  <Alert kind={kinds.DANGER}>
                    <div>{translate('UnableToLoadRootFolders')}</div>
                    <Button onPress={onRetryRootFolders}>{translate('Retry')}</Button>
                  </Alert>
                )}
                {(isRootFoldersPopulated || !addMissingAuthors) && renderDualMediaImportListFields()}
              </FieldSet>

              {
                !!providerSpecificFields.length &&
                  <FieldSet legend={translate('ImportListSpecificSettings')}>
                    {providerSpecificFields.map(renderProviderField)}
                  </FieldSet>
              }

            </Form>
        }
      </ModalBody>
      <ModalFooter>
        {
          id &&
            <Button
              className={styles.deleteButton}
              kind={kinds.DANGER}
              onPress={onDeleteImportListPress}
            >
              {translate('Delete')}
            </Button>
        }

        <AdvancedSettingsButton
          advancedSettings={advancedSettings}
          onAdvancedSettingsPress={onAdvancedSettingsPress}
          showLabel={false}
        />

        <SpinnerErrorButton
          isSpinning={isTesting}
          error={saveError}
          onPress={onTestPress}
          isDisabled={isGoodreadsValidationDisabled}
        >
          {translate('Test')}
        </SpinnerErrorButton>

        <Button
          onPress={onModalClose}
        >
          {translate('Cancel')}
        </Button>

        <SpinnerErrorButton
          isSpinning={isSaving}
          error={saveError}
          onPress={onSavePress}
          isDisabled={isSaveDisabled}
        >
          {saveLabel}
        </SpinnerErrorButton>
      </ModalFooter>
    </ModalContent>
  );
}

EditImportListModalContent.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isRootFoldersPopulated: PropTypes.bool.isRequired,
  rootFoldersError: PropTypes.object,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  isTesting: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  monitoringDefaults: PropTypes.object,
  onInputChange: PropTypes.func.isRequired,
  onFieldChange: PropTypes.func.isRequired,
  onRetryRootFolders: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onTestPress: PropTypes.func.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onDeleteImportListPress: PropTypes.func
};

export default EditImportListModalContent;
