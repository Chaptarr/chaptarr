import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorMetadataProfilePopoverContent from 'AddAuthor/AuthorMetadataProfilePopoverContent';
import AuthorMonitoringGatePopoverContent from 'AddAuthor/AuthorMonitoringGatePopoverContent';
import AuthorMonitorNewItemsOptionsPopoverContent from 'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent';
import MoveAuthorModal from 'Author/MoveAuthor/MoveAuthorModal';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Popover from 'Components/Tooltip/Popover';
import { icons, inputTypes, kinds, tooltipPositions } from 'Helpers/Props';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import monitorNewItemsOptions, {
  normalizeMonitorNewItemsOption
} from 'Utilities/Author/monitorNewItemsOptions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import styles from './EditAuthorModalContent.css';

class EditAuthorModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    const { hasAudiobookRootFolder, hasEbookRootFolder } = props;

    // Determine initial media type based on available root folders (or a caller override)
    const requestedMediaType = props.initialMediaType;
    let initialMediaType = requestedMediaType;
    if (initialMediaType !== 'audiobook' && initialMediaType !== 'ebook') {
      initialMediaType = hasAudiobookRootFolder || !hasEbookRootFolder ? 'audiobook' : 'ebook';
    }

    this.state = {
      isConfirmMoveModalOpen: false,
      hasAudiobookRootFolder,
      hasEbookRootFolder,
      selectedMediaType: initialMediaType
    };
  }

  //
  // Listeners

  onSavePress = () => {
    const {
      isPathChanging,
      onSavePress
    } = this.props;

    if (isPathChanging && !this.state.isConfirmMoveModalOpen) {
      this.setState({ isConfirmMoveModalOpen: true });
    } else {
      this.setState({ isConfirmMoveModalOpen: false });

      onSavePress(false);
    }
  };

  onMoveAuthorPress = () => {
    this.setState({ isConfirmMoveModalOpen: false });

    this.props.onSavePress(true);
  };

  onMediaTypeChange = (mediaType) => {
    this.setState({ selectedMediaType: mediaType });
  };

  //
  // Render

  render() {
    const {
      authorName,
      item,
      isSaving,
      saveError,
      showMetadataProfile,
      originalPath,
      isWindows,
      rootFolders,
      onInputChange,
      onModalClose,
      onDeleteAuthorPress,
      ...otherProps
    } = this.props;

    const {
      audiobookMonitored,
      audiobookMonitorNewItems,
      ebookMonitored,
      ebookMonitorNewItems,
      syncMonitoredAcrossFormats,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      path,
      audiobookPath,
      ebookPath,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      tags,
      audiobookTags,
      ebookTags
    } = item;

    const {
      hasAudiobookRootFolder,
      hasEbookRootFolder,
      selectedMediaType
    } = this.state;

    const supportsMediaType = (rootFolderPath, mediaType) => {
      if (!rootFolderPath) {
        return false;
      }

      const rootFolder = (rootFolders || []).find((folder) => folder.path === rootFolderPath);
      if (!rootFolder) {
        return false;
      }

      const folderType = coerceFolderType(rootFolder.folderType);

      return folderType === FolderType.Mixed ||
        (mediaType === 'audiobook' && folderType === FolderType.Audiobook) ||
        (mediaType === 'ebook' && folderType === FolderType.Ebook);
    };

    const canSyncMonitoredAcrossFormats =
      supportsMediaType(audiobookRootFolderPath?.value, 'audiobook') &&
      supportsMediaType(ebookRootFolderPath?.value, 'ebook');

    const isSyncMonitoredAcrossFormatsChecked = syncMonitoredAcrossFormats?.value === true;
    const syncMonitoredAcrossFormatsDisabled = !canSyncMonitoredAcrossFormats && !isSyncMonitoredAcrossFormatsChecked;

    const syncMonitoredAcrossFormatsHelpText = canSyncMonitoredAcrossFormats ?
      'When enabled, monitored books are synced across formats immediately. This does not trigger a search.' :
      'Requires both audiobook and ebook root folders configured for this author. Add the missing root folder, or clear this setting if it was enabled earlier.';

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('EditAuthorHeader', { authorName })}
        </ModalHeader>

        <ModalBody>
          {
            saveError &&
              <Alert kind={kinds.DANGER}>
                {getErrorMessage(saveError, 'Unable to save author')}
              </Alert>
          }

          <Form {...otherProps}>
            <FormGroup>
              <FormLabel>
                {translate('SyncMonitoredAudioEbooks')}
              </FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="syncMonitoredAcrossFormats"
                helpText={syncMonitoredAcrossFormatsHelpText}
                isDisabled={syncMonitoredAcrossFormatsDisabled}
                {...(syncMonitoredAcrossFormats || { value: false })}
                onChange={onInputChange}
              />
            </FormGroup>

            <MediaTypeToggle
              selectedMediaType={selectedMediaType}
              onMediaTypeChange={this.onMediaTypeChange}
              hasAudiobookRootFolder={hasAudiobookRootFolder}
              hasEbookRootFolder={hasEbookRootFolder}
            />

            {selectedMediaType === 'audiobook' ? (
              // Audiobook Settings
              <>
                <FormGroup>
                  <FormLabel>
                    {translate('MonitorAuthorAudiobooks')}
                    <Popover
                      anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                      title={translate('MonitorAuthorAudiobooks')}
                      body={<AuthorMonitoringGatePopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="audiobookMonitored"
                    {...audiobookMonitored}
                    value={audiobookMonitored?.value === true}
                    onChange={onInputChange}
                  />
                </FormGroup>

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
                    name="audiobookMonitorNewItems"
                    values={monitorNewItemsOptions}
                    {...audiobookMonitorNewItems}
                    value={normalizeMonitorNewItemsOption(audiobookMonitorNewItems?.value)}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('AudiobookRootFolder')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.ROOT_FOLDER_SELECT}
                    name="audiobookRootFolderPath"
                    valueOptions={{
                      authorFolder: authorName,
                      isWindows
                    }}
                    selectedValueOptions={{
                      authorFolder: authorName,
                      isWindows
                    }}
                    helpText={translate('EditAuthorAudiobookRootFolderHelpText')}
                    folderType={1}
                    includeMissingValue={true}
                    {...audiobookRootFolderPath}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('AudiobookQualityProfile')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.QUALITY_PROFILE_SELECT}
                    name="audiobookQualityProfileId"
                    helpText={translate('AudiobookQualityProfileHelpText')}
                    includeNone={true}
                    noneLabel={translate('EditAuthorNoAudiobooksOption')}
                    profileType="audiobook"
                    {...audiobookQualityProfileId}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('AudiobookMetadataProfile')}
                    <Popover
                      anchor={
                        <Icon
                          className={styles.labelIcon}
                          name={icons.INFO}
                        />
                      }
                      title={translate('AudiobookMetadataProfile')}
                      body={<AuthorMetadataProfilePopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.METADATA_PROFILE_SELECT}
                    name="audiobookMetadataProfileId"
                    helpText={translate('AudiobookMetadataProfileHelpText')}
                    includeNone={true}
                    profileType="audiobook"
                    {...audiobookMetadataProfileId}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('AudiobookPath')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.PATH}
                    name="audiobookPath"
                    helpText={translate('AudiobookPathHelpText')}
                    {...(audiobookPath || { value: item.audiobookRootFolderPath?.value ? `${item.audiobookRootFolderPath.value}/${authorName}` : '' })}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('Tags')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.TAG}
                    name="audiobookTags"
                    {...(audiobookTags || tags)}
                    onChange={onInputChange}
                  />
                </FormGroup>
              </>
            ) : (
              // Ebook Settings
              <>
                <FormGroup>
                  <FormLabel>
                    {translate('MonitorAuthorEbooks')}
                    <Popover
                      anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                      title={translate('MonitorAuthorEbooks')}
                      body={<AuthorMonitoringGatePopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="ebookMonitored"
                    {...ebookMonitored}
                    value={ebookMonitored?.value === true}
                    onChange={onInputChange}
                  />
                </FormGroup>

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
                    name="ebookMonitorNewItems"
                    values={monitorNewItemsOptions}
                    {...ebookMonitorNewItems}
                    value={normalizeMonitorNewItemsOption(ebookMonitorNewItems?.value)}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('EbookRootFolder')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.ROOT_FOLDER_SELECT}
                    name="ebookRootFolderPath"
                    valueOptions={{
                      authorFolder: authorName,
                      isWindows
                    }}
                    selectedValueOptions={{
                      authorFolder: authorName,
                      isWindows
                    }}
                    helpText={translate('EditAuthorEbookRootFolderHelpText')}
                    folderType={2}
                    includeMissingValue={true}
                    {...ebookRootFolderPath}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('EbookQualityProfile')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.QUALITY_PROFILE_SELECT}
                    name="ebookQualityProfileId"
                    helpText={translate('EbookQualityProfileHelpText')}
                    includeNone={true}
                    noneLabel={translate('EditAuthorNoEbooksOption')}
                    profileType="ebook"
                    {...ebookQualityProfileId}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('EbookMetadataProfile')}
                    <Popover
                      anchor={
                        <Icon
                          className={styles.labelIcon}
                          name={icons.INFO}
                        />
                      }
                      title={translate('EbookMetadataProfile')}
                      body={<AuthorMetadataProfilePopoverContent />}
                      position={tooltipPositions.RIGHT}
                    />
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.METADATA_PROFILE_SELECT}
                    name="ebookMetadataProfileId"
                    helpText={translate('EbookMetadataProfileHelpText')}
                    includeNone={true}
                    profileType="ebook"
                    {...ebookMetadataProfileId}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('EbookPath')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.PATH}
                    name="ebookPath"
                    helpText={translate('EbookPathHelpText')}
                    {...(ebookPath || { value: item.ebookRootFolderPath?.value ? `${item.ebookRootFolderPath.value}/${authorName}` : '' })}
                    onChange={onInputChange}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel>
                    {translate('Tags')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.TAG}
                    name="ebookTags"
                    {...(ebookTags || tags)}
                    onChange={onInputChange}
                  />
                </FormGroup>
              </>
            )}
          </Form>
        </ModalBody>
        <ModalFooter>
          <Button
            className={styles.deleteButton}
            kind={kinds.DANGER}
            onPress={onDeleteAuthorPress}
          >
            {translate('Delete')}
          </Button>

          <Button
            onPress={onModalClose}
          >
            {translate('Cancel')}
          </Button>

          <SpinnerButton
            isSpinning={isSaving}
            onPress={this.onSavePress}
          >
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>

        <MoveAuthorModal
          originalPath={originalPath}
          destinationPath={path.value}
          isOpen={this.state.isConfirmMoveModalOpen}
          onSavePress={this.onSavePress}
          onMoveAuthorPress={this.onMoveAuthorPress}
        />

      </ModalContent>
    );
  }
}

EditAuthorModalContent.propTypes = {
  authorId: PropTypes.number.isRequired,
  authorName: PropTypes.string.isRequired,
  item: PropTypes.object.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  showMetadataProfile: PropTypes.bool.isRequired,
  isPathChanging: PropTypes.bool.isRequired,
  originalPath: PropTypes.string.isRequired,
  hasAudiobookRootFolder: PropTypes.bool.isRequired,
  hasEbookRootFolder: PropTypes.bool.isRequired,
  rootFolders: PropTypes.array,
  initialMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  isWindows: PropTypes.bool,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteAuthorPress: PropTypes.func.isRequired
};

export default EditAuthorModalContent;
