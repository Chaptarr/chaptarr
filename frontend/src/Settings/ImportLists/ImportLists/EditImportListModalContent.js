import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import AuthorMonitorNewItemsOptionsPopoverContent from 'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent';
import Alert from 'Components/Alert';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
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
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './EditImportListModalContent.css';

function ImportListMonitoringOptionsPopoverContent() {
  return (
    <>
      <Alert>
        {translate('ShouldMonitorHelpText')}
      </Alert>

      <DescriptionList>
        <DescriptionListItem
          title={translate('None')}
          data={translate('DataListMonitorNone')}
        />

        <DescriptionListItem
          title={translate('SpecificBook')}
          data={translate('DataListMonitorSpecificBook')}
        />

        <DescriptionListItem
          title={translate('AllAuthorBooks')}
          data={translate('DataListMonitorAll')}
        />
      </DescriptionList>
    </>
  );
}

function EditImportListModalContent(props) {

  const monitorOptions = [
    { key: 'none', value: translate('None') },
    { key: 'specificBook', value: translate('SpecificBook') },
    { key: 'entireAuthor', value: translate('AllAuthorBooks') }
  ];

  const {
    advancedSettings,
    isFetching,
    error,
    isSaving,
    isTesting,
    saveError,
    item,
    onInputChange,
    onFieldChange,
    onModalClose,
    onSavePress,
    onTestPress,
    onAdvancedSettingsPress,
    onDeleteImportListPress,
    showMetadataProfile,
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
    rootFolderPath,
    monitorNewItems,
    qualityProfileId,
    metadataProfileId,
    tags,
    fields,
    message
  } = item;

  const isHardcoverLibraryImportList = (implementation?.value === 'HardcoverLibraryImportList') ||
    (implementationName === 'Hardcover Library');

  const isGoodreadsBookshelfImportList = (implementation?.value === 'GoodreadsBookshelf') ||
    (implementationName === 'Goodreads Bookshelves');

  const isGoodreadsListImportList = (implementation?.value === 'GoodreadsListImportList') ||
    (implementationName === 'Goodreads List');

  const isGoodreadsSeriesImportList = (implementation?.value === 'GoodreadsSeriesImportList') ||
    (implementationName === 'Goodreads Series');

  const isGoodreadsDualMediaImportList = isGoodreadsBookshelfImportList ||
    isGoodreadsListImportList ||
    isGoodreadsSeriesImportList;

  const isDualMediaImportList = isHardcoverLibraryImportList || isGoodreadsDualMediaImportList;

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
    const monitorAudiobooksField = getField('monitorAudiobooks');
    const monitorEbooksField = getField('monitorEbooks');

    const hasAudiobookSettings = !!monitorAudiobooksField;
    const hasEbookSettings = !!monitorEbooksField;

    if (!hasAudiobookSettings && hasEbookSettings) {
      return 'ebook';
    }

    if (hasAudiobookSettings && !hasEbookSettings) {
      return 'audiobook';
    }

    if (!hasAudiobookSettings && !hasEbookSettings) {
      return 'audiobook';
    }

    const includesAudiobooks = monitorAudiobooksField.value == null ? true : !!monitorAudiobooksField.value;
    const includesEbooks = monitorEbooksField.value == null ? true : !!monitorEbooksField.value;

    if (includesAudiobooks && includesEbooks) {
      return 'both';
    }

    if (includesEbooks) {
      return 'ebook';
    }

    return 'audiobook';
  };

  const dualMediaSelectedMediaType = isDualMediaImportList ? getDualMediaSelectedMediaType() : 'audiobook';
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
    (isDualMediaImportList &&
      ((dualMediaSaveNeedsAudiobooks && !dualMediaAudiobookRootFolderPath) ||
        (dualMediaSaveNeedsEbooks && !dualMediaEbookRootFolderPath))) ||
    isGoodreadsValidationDisabled;

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
    if (id || !isDualMediaImportList) {
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

  const renderDualMediaImportListFields = () => {
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

    const providerSpecificFields = fields.filter((f) => !mediaFieldNames.has(f.name));

    const monitorAudiobooksField = getField('monitorAudiobooks');
    const monitorEbooksField = getField('monitorEbooks');

    const hasAudiobookSettings = !!monitorAudiobooksField;
    const hasEbookSettings = !!monitorEbooksField;

    const hasMediaToggle = hasAudiobookSettings && hasEbookSettings;
    let activeMediaType = dualMediaSelectedMediaType;
    if (!hasMediaToggle) {
      activeMediaType = hasAudiobookSettings ? 'audiobook' : 'ebook';
    }

    const onMediaTypeChange = (mediaType) => {
      if (monitorAudiobooksField) {
        onFieldChange({ name: 'monitorAudiobooks', value: mediaType !== 'ebook' });
      }

      if (monitorEbooksField) {
        onFieldChange({ name: 'monitorEbooks', value: mediaType !== 'audiobook' });
      }
    };

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
      if (!hasAudiobookSettings) {
        return null;
      }

      return (
        <div className={styles.hardcoverSubsection}>
          {renderRootFolderField(audiobookRootFolderField, FolderType.Audiobook)}
          {fields.filter((f) => f.name === 'audiobookQualityProfileId' || f.name === 'audiobookMetadataProfileId' || f.name === 'audiobookTags').map(renderProviderField)}
        </div>
      );
    };

    const renderEbookFields = () => {
      if (!hasEbookSettings) {
        return null;
      }

      return (
        <div className={styles.hardcoverSubsection}>
          {renderRootFolderField(ebookRootFolderField, FolderType.Ebook)}
          {fields.filter((f) => f.name === 'ebookQualityProfileId' || f.name === 'ebookMetadataProfileId' || f.name === 'ebookTags').map(renderProviderField)}
        </div>
      );
    };

    return (
      <>
        {providerSpecificFields.map(renderProviderField)}

        {
          hasMediaToggle &&
            <MediaTypeToggle
              className={styles.hardcoverMediaTypeToggle}
              selectedMediaType={activeMediaType}
              onMediaTypeChange={onMediaTypeChange}
              includeBoth={true}
            />
        }

        {activeMediaType === 'audiobook' ? renderAudiobookFields() : null}
        {activeMediaType === 'ebook' ? renderEbookFields() : null}
        {activeMediaType === 'both' ? (
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

          <div>
            {translate('ImportListHardcoverTrackingExplainerPrefix')} <strong>{translate('Book')}</strong> {translate('ImportListHardcoverTrackingExplainerMid')} <strong>{translate('Edition')}</strong> {translate('ImportListHardcoverTrackingExplainerSuffix')}
          </div>
          <div>
            {translate('ImportListHardcoverWantToReadExplainerPrefix')} <strong>{translate('ImportListHardcoverWantToReadName')}</strong>{translate('ImportListHardcoverWantToReadExplainerMid')} <strong>{translate('ImportListHardcoverOwnedName')}</strong> {translate('ImportListHardcoverWantToReadExplainerSuffix')}
          </div>
          <div>
            {translate('ImportListHardcoverOwnedExplainerPrefix')} <strong>{translate('Audiobooks')}</strong> {translate('ImportListHardcoverOwnedExplainerMid')} <strong>{translate('Ebooks')}</strong>{translate('ImportListHardcoverOwnedExplainerSuffix')}
          </div>

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
                !!goodreadsValidationMessage &&
                  <Alert
                    className={styles.message}
                    kind={kinds.WARNING}
                    role="alert"
                  >
                    {goodreadsValidationMessage}
                  </Alert>
              }

              {renderHardcoverIntro()}

              <FieldSet legend={translate('ImportListSettings')} >
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
                    {translate('EnableAutomaticAdd')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="enableAutomaticAdd"
                    helpText={translate('EnableAutomaticAddHelpText')}
                    {...enableAutomaticAdd}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('ImportListMonitoring')}

                    <Popover
                      anchor={
                        <Icon
                          className={styles.labelIcon}
                          name={icons.INFO}
                        />
                      }
                      title={translate('MonitoringOptions')}
                      body={<ImportListMonitoringOptionsPopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="shouldMonitor"
                    values={monitorOptions}
                    {...shouldMonitor}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('ShouldMonitorExisting')}

                    <Popover
                      anchor={
                        <Icon
                          className={styles.labelIcon}
                          name={icons.INFO}
                        />
                      }
                      title={translate('ShouldMonitorExisting')}
                      body={<Alert>{translate('ShouldMonitorExistingHelpText')}</Alert>}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="shouldMonitorExisting"
                    {...shouldMonitorExisting}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('SearchForNewItems')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="shouldSearch"
                    helpText={translate('ShouldSearchHelpText')}
                    {...shouldSearch}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('MonitorNewItems')}
                    <Popover
                      anchor={
                        <Icon
                          className={styles.labelIcon}
                          name={icons.INFO}
                        />
                      }
                      title={translate('MonitorNewItems')}
                      body={<AuthorMonitorNewItemsOptionsPopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.MONITOR_NEW_ITEMS_SELECT}
                    name="monitorNewItems"
                    {...monitorNewItems}
                    onChange={onInputChange}
                  />
                </FormGroup>
              </FieldSet>

              {
                !isDualMediaImportList &&
                  <FieldSet legend={translate('AddedAuthorSettings')} >
                    <FormGroup>
                      <FormLabel>
                        {translate('RootFolder')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.ROOT_FOLDER_SELECT}
                        name="rootFolderPath"
                        helpText={translate('RootFolderPathHelpText')}
                        {...rootFolderPath}
                        includeMissingValue={true}
                        onChange={onInputChange}
                      />
                    </FormGroup>

                    <FormGroup>
                      <FormLabel>
                        {translate('QualityProfile')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.QUALITY_PROFILE_SELECT}
                        name="qualityProfileId"
                        helpText={translate('QualityProfileIdHelpText')}
                        {...qualityProfileId}
                        onChange={onInputChange}
                      />
                    </FormGroup>

                    <FormGroup className={showMetadataProfile ? undefined : styles.hideMetadataProfile}>
                      <FormLabel>
                        {translate('MetadataProfile')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.METADATA_PROFILE_SELECT}
                        name="metadataProfileId"
                        helpText={translate('MetadataProfileIdHelpText')}
                        {...metadataProfileId}
                        includeNone={true}
                        onChange={onInputChange}
                      />
                    </FormGroup>

                    <FormGroup>
                      <FormLabel>
                        {translate('ChaptarrTags')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.TAG}
                        name="tags"
                        helpText={translate('TagsHelpText')}
                        {...tags}
                        onChange={onInputChange}
                      />
                    </FormGroup>
                  </FieldSet>
              }

              {
                !!fields && !!fields.length &&
                  <FieldSet legend={translate('ImportListSpecificSettings')} >
                    {
                      isDualMediaImportList ?
                        renderDualMediaImportListFields() :
                        fields.map(renderProviderField)
                    }
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
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  isTesting: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  showMetadataProfile: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onFieldChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onTestPress: PropTypes.func.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onDeleteImportListPress: PropTypes.func
};

export default EditImportListModalContent;
