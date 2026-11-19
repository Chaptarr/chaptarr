import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputButton from 'Components/Form/FormInputButton';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import RemotePathMappingsConnector from 'Settings/DownloadClients/RemotePathMappings/RemotePathMappingsConnector';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import NamingConnector from './Naming/NamingConnector';
import RootFoldersConnector from './RootFolder/RootFoldersConnector';

const rescanAfterRefreshOptions = [
  { key: 'always', value: 'Always' },
  { key: 'afterManual', value: 'After Manual Refresh' },
  { key: 'never', value: 'Never' }
];

const allowFingerprintingOptions = [
  { key: 'allFiles', value: 'Always' },
  { key: 'newFiles', value: 'For new imports only' },
  { key: 'never', value: 'Never' }
];

const fileDateOptions = [
  { key: 'none', value: 'None' },
  { key: 'bookReleaseDate', value: 'Book Release Date' }
];

function getMatchingStrictnessHelpText(value) {
  switch (value) {
    case 'strict':
      return translate('BookMatchingStrictnessStrictHelpText');
    case 'aggressive':
      return translate('BookMatchingStrictnessAggressiveHelpText');
    case 'balanced':
    default:
      return translate('BookMatchingStrictnessBalancedHelpText');
  }
}

class MediaManagement extends Component {

  //
  // Render

  render() {
    const {
      advancedSettings,
      isFetching,
      error,
      settings,
      hasSettings,
      isWindows,
      isCoverDownloadScopeConfirmOpen,
      coverCleanupEstimate,
      isFetchingCoverCleanupEstimate,
      coverCleanupEstimateError,
      onInputChange,
      onSavePress,
      onConfirmCoverDownloadScope,
      onCancelCoverDownloadScope,
      onCalculateCoverSavings,
      ...otherProps
    } = this.props;

    const bookMatchingStrictness = settings.bookMatchingStrictness || { value: 'balanced' };
    const usePathAsTagsFallback = settings.usePathAsTagsFallback || { value: true };
    const autoAddMissingAuthorsFromCompletedDownloads = settings.autoAddMissingAuthorsFromCompletedDownloads || { value: false };
    const coverDownloadScope = settings.coverDownloadScope || { value: 'allBooks' };
    const coverDownloadScopeOptions = [
      { key: 'allBooks', value: translate('CoverDownloadScopeAllBooks') },
      { key: 'libraryBooks', value: translate('CoverDownloadScopeLibraryBooks') }
    ];
    const isStrictMatching = bookMatchingStrictness.value === 'strict';
    let coverCleanupMessage = translate('CoverCleanupEstimateHelpText');

    if (isFetchingCoverCleanupEstimate) {
      coverCleanupMessage = translate('CoverCleanupEstimateLoading');
    } else if (coverCleanupEstimateError) {
      coverCleanupMessage = translate('CoverCleanupEstimateError');
    } else if (coverCleanupEstimate) {
      coverCleanupMessage = translate('CoverCleanupEstimate', { size: formatBytes(coverCleanupEstimate.bytes, false, 0) });
    }

    return (
      <PageContent title={translate('MediaManagementSettings')}>
        <SettingsToolbarConnector
          advancedSettings={advancedSettings}
          {...otherProps}
          onSavePress={onSavePress}
        />

        <PageContentBody>
          <RootFoldersConnector />
          <RemotePathMappingsConnector />
          <NamingConnector
            hasMediaManagementSettings={hasSettings}
            mediaManagementSettings={settings}
            onMediaManagementInputChange={onInputChange}
          />

          {
            isFetching &&
              <FieldSet legend={translate('NamingSettings')}>
                <LoadingIndicator />
              </FieldSet>
          }

          {
            !isFetching && error &&
              <FieldSet legend={translate('NamingSettings')}>
                <Alert kind={kinds.DANGER}>
                  {translate('UnableToLoadMediaManagementSettings')}
                </Alert>
              </FieldSet>
          }

          {
            hasSettings && !isFetching && !error &&
              <Form
                id="mediaManagementSettings"
                {...otherProps}
              >
                {
                  advancedSettings &&
                    <FieldSet legend={translate('Folders')}>
                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>
                          {translate('DeleteEmptyFolders')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="deleteEmptyFolders"
                          helpText={translate('DeleteEmptyFoldersHelpText')}
                          onChange={onInputChange}
                          {...settings.deleteEmptyFolders}
                        />
                      </FormGroup>
                    </FieldSet>
                }

                {
                  advancedSettings &&
                    <FieldSet
                      legend={translate('Importing')}
                    >
                      {
                        !isWindows &&
                          <FormGroup
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                            size={sizes.MEDIUM}
                          >
                            <FormLabel>
                              {translate('SkipFreeSpaceCheck')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.CHECK}
                              name="skipFreeSpaceCheckWhenImporting"
                              helpText={translate('SkipFreeSpaceCheckWhenImportingHelpText')}
                              onChange={onInputChange}
                              {...settings.skipFreeSpaceCheckWhenImporting}
                            />
                          </FormGroup>
                      }

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>
                          {translate('MinimumFreeSpace')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.NUMBER}
                          unit='MB'
                          name="minimumFreeSpaceWhenImporting"
                          helpText={translate('MinimumFreeSpaceWhenImportingHelpText')}
                          onChange={onInputChange}
                          {...settings.minimumFreeSpaceWhenImporting}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>
                          {translate('UseHardlinksInsteadOfCopy')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="copyUsingHardlinks"
                          helpText={translate('CopyUsingHardlinksHelpText')}
                          helpTextWarning={translate('CopyUsingHardlinksHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.copyUsingHardlinks}
                        />
                      </FormGroup>

                      <FormGroup size={sizes.MEDIUM}>
                        <FormLabel>
                          {translate('ImportExtraFiles')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="importExtraFiles"
                          helpText={translate('ImportExtraFilesHelpText')}
                          onChange={onInputChange}
                          {...settings.importExtraFiles}
                        />
                      </FormGroup>

                      {
                        settings.importExtraFiles.value ?
                          <FormGroup
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                          >
                            <FormLabel>{translate('ImportExtraFiles')}</FormLabel>

                            <FormInputGroup
                              type={inputTypes.TEXT}
                              name="extraFileExtensions"
                              helpTexts={[
                                translate('ExtraFileExtensionsHelpText'),
                                translate('ExtraFileExtensionsHelpTextsExamples')
                              ]}
                              onChange={onInputChange}
                              {...settings.extraFileExtensions}
                            />
                          </FormGroup> : null
                      }
                    </FieldSet>
                }

                <FieldSet
                  legend={translate('Matching')}
                >
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('BookMatchingStrictness')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="bookMatchingStrictness"
                      helpText={getMatchingStrictnessHelpText(bookMatchingStrictness.value)}
                      values={[
                        { key: 'strict', value: translate('BookMatchingStrictnessStrict') },
                        { key: 'balanced', value: translate('BookMatchingStrictnessBalanced') },
                        { key: 'aggressive', value: translate('BookMatchingStrictnessAggressive') }
                      ]}
                      onChange={onInputChange}
                      {...bookMatchingStrictness}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('UsePathAsTagsFallback')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="usePathAsTagsFallback"
                      helpText={
                        isStrictMatching ?
                          translate('UsePathAsTagsFallbackStrictDisabledHelpText') :
                          translate('UsePathAsTagsFallbackHelpText')
                      }
                      isDisabled={isStrictMatching}
                      onChange={onInputChange}
                      {...usePathAsTagsFallback}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('AutoAddMissingAuthorsFromCompletedDownloads')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="autoAddMissingAuthorsFromCompletedDownloads"
                      helpText={translate('AutoAddMissingAuthorsFromCompletedDownloadsHelpText')}
                      onChange={onInputChange}
                      {...autoAddMissingAuthorsFromCompletedDownloads}
                    />
                  </FormGroup>
                </FieldSet>

                <FieldSet
                  legend={translate('CoverDownloadSettings')}
                >
                  <FormGroup size={sizes.MEDIUM} aria-live="polite">
                    <FormLabel>
                      {translate('CoverDownloadScope')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="coverDownloadScope"
                      helpTexts={[
                        translate(coverDownloadScope.value === 'libraryBooks' ? 'CoverDownloadScopeLibraryBooksHelpText' : 'CoverDownloadScopeAllBooksHelpText'),
                        coverCleanupMessage
                      ]}
                      buttons={
                        <FormInputButton
                          canSpin={true}
                          kind={kinds.DEFAULT}
                          aria-label={translate('CalculateCoverSavings')}
                          isSpinning={isFetchingCoverCleanupEstimate}
                          onPress={onCalculateCoverSavings}
                        >
                          {translate('CalculateCoverSavings')}
                        </FormInputButton>
                      }
                      values={coverDownloadScopeOptions}
                      onChange={onInputChange}
                      {...coverDownloadScope}
                    />
                  </FormGroup>
                </FieldSet>

                <FieldSet
                  legend={translate('FileManagement')}
                >
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('IgnoreDeletedBooks')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="autoUnmonitorPreviouslyDownloadedBooks"
                      helpText={translate('AutoUnmonitorPreviouslyDownloadedBooksHelpText')}
                      onChange={onInputChange}
                      {...settings.autoUnmonitorPreviouslyDownloadedBooks}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>
                      {translate('WatchRootFoldersForFileChanges')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="watchLibraryForChanges"
                      helpText={translate('WatchLibraryForChangesHelpText')}
                      onChange={onInputChange}
                      {...settings.watchLibraryForChanges}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>
                      {translate('RescanAuthorFolderAfterRefresh')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="rescanAfterRefresh"
                      helpText={translate('RescanAfterRefreshHelpText')}
                      helpTextWarning={translate('RescanAfterRefreshHelpTextWarning')}
                      values={rescanAfterRefreshOptions}
                      onChange={onInputChange}
                      {...settings.rescanAfterRefresh}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>
                      {translate('AllowFingerprinting')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="allowFingerprinting"
                      helpText={translate('AllowFingerprintingHelpText')}
                      helpTextWarning={translate('AllowFingerprintingHelpTextWarning')}
                      values={allowFingerprintingOptions}
                      onChange={onInputChange}
                      {...settings.allowFingerprinting}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>
                      {translate('ChangeFileDate')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="fileDate"
                      helpText={translate('FileDateHelpText')}
                      values={fileDateOptions}
                      onChange={onInputChange}
                      {...settings.fileDate}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>
                      {translate('RecyclingBin')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.PATH}
                      name="recycleBin"
                      helpText={translate('RecycleBinHelpText')}
                      onChange={onInputChange}
                      {...settings.recycleBin}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>
                      {translate('RecyclingBinCleanup')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.NUMBER}
                      name="recycleBinCleanupDays"
                      helpText={translate('RecycleBinCleanupDaysHelpText')}
                      helpTextWarning={translate('RecycleBinCleanupDaysHelpTextWarning')}
                      min={0}
                      onChange={onInputChange}
                      {...settings.recycleBinCleanupDays}
                    />
                  </FormGroup>
                </FieldSet>

                {
                  advancedSettings && !isWindows &&
                    <FieldSet
                      legend={translate('Permissions')}
                    >
                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>
                          {translate('SetPermissions')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="setPermissionsLinux"
                          helpText={translate('SetPermissionsLinuxHelpText')}
                          helpTextWarning={translate('SetPermissionsLinuxHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.setPermissionsLinux}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                      >
                        <FormLabel>
                          {translate('ChmodFolder')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.UMASK}
                          name="chmodFolder"
                          helpText={translate('ChmodFolderHelpText')}
                          helpTextWarning={translate('ChmodFolderHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.chmodFolder}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                      >
                        <FormLabel>
                          {translate('ChownGroup')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.TEXT}
                          name="chownGroup"
                          helpText={translate('ChownGroupHelpText')}
                          helpTextWarning={translate('ChownGroupHelpTextWarning')}
                          values={fileDateOptions}
                          onChange={onInputChange}
                          {...settings.chownGroup}
                        />
                      </FormGroup>
                    </FieldSet>
                }
              </Form>
          }
        </PageContentBody>

        <ConfirmModal
          isOpen={isCoverDownloadScopeConfirmOpen}
          title={translate('CoverDownloadScopeConfirmTitle')}
          message={
            <>
              <p>{translate('CoverDownloadScopeConfirmMessage')}</p>
              {
                (coverCleanupEstimate || isFetchingCoverCleanupEstimate || coverCleanupEstimateError) &&
                  <p aria-live="polite">{coverCleanupMessage}</p>
              }
            </>
          }
          confirmLabel={translate('Save')}
          onConfirm={onConfirmCoverDownloadScope}
          onCancel={onCancelCoverDownloadScope}
        />
      </PageContent>
    );
  }

}

MediaManagement.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  isWindows: PropTypes.bool.isRequired,
  isCoverDownloadScopeConfirmOpen: PropTypes.bool.isRequired,
  coverCleanupEstimate: PropTypes.shape({ bytes: PropTypes.number.isRequired }),
  isFetchingCoverCleanupEstimate: PropTypes.bool.isRequired,
  coverCleanupEstimateError: PropTypes.bool.isRequired,
  onConfirmCoverDownloadScope: PropTypes.func.isRequired,
  onCancelCoverDownloadScope: PropTypes.func.isRequired,
  onCalculateCoverSavings: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default MediaManagement;
