import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorMetadataProfilePopoverContent from 'AddAuthor/AuthorMetadataProfilePopoverContent';
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
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Popover from 'Components/Tooltip/Popover';
import { calibreProfiles, icons, inputTypes, kinds, tooltipPositions } from 'Helpers/Props';
import { coerceFolderType, FolderType } from 'Helpers/Props/folderTypes';
import monitorNewItemsOptions from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions from 'Utilities/Author/monitorOptions';
import translate from 'Utilities/String/translate';
import styles from './EditRootFolderModalContent.css';

// Temporary toggle: hide Calibre controls in the GUI until Calibre support is restored.
const SHOW_CALIBRE_UI = false;

class EditRootFolderModalContent extends Component {
  constructor(props, context) {
    super(props, context);

    const folderTypeValue = coerceFolderType(props.folderTypeProp ?? props.item?.folderType?.value);
    const initialMediaType = folderTypeValue === FolderType.Ebook ? 'ebook' : 'audiobook';

    this.state = {
      initialFolderType: folderTypeValue,
      selectedMediaType: initialMediaType,
      hasViewedAudiobookTab: initialMediaType === 'audiobook',
      hasViewedEbookTab: initialMediaType === 'ebook'
    };
  }

  isFolderTypeFixed = () => {
    const rootFolderId = this.props.item?.id?.value;
    const folderTypePropValue = coerceFolderType(this.props.folderTypeProp);

    return !rootFolderId &&
      (folderTypePropValue === FolderType.Audiobook || folderTypePropValue === FolderType.Ebook);
  };

  onMediaTypeChange = (mediaType) => {
    if (this.isFolderTypeFixed()) {
      return;
    }

    if (mediaType === this.state.selectedMediaType) {
      return;
    }

    const newState = { selectedMediaType: mediaType };
    if (mediaType === 'audiobook') {
      newState.hasViewedAudiobookTab = true;
    } else if (mediaType === 'ebook') {
      newState.hasViewedEbookTab = true;
    }

    const rootFolderId = this.props.item?.id?.value;
    const folderTypeValue = this.props.item?.folderType?.value;
    if (!rootFolderId && folderTypeValue !== FolderType.Mixed) {
      const nextFolderType = mediaType === 'audiobook' ? FolderType.Audiobook : FolderType.Ebook;
      this.props.onInputChange({ name: 'folderType', value: nextFolderType });
    }

    this.setState(newState);
  };

  onAcceptsMixedContentChange = ({ value }) => {
    if (this.isFolderTypeFixed()) {
      return;
    }

    const rootFolderId = this.props.item?.id?.value;
    const { selectedMediaType, initialFolderType } = this.state;

    let nextFolderType = selectedMediaType === 'audiobook' ? FolderType.Audiobook : FolderType.Ebook;
    let nextSelectedMediaType = selectedMediaType;

    if (value) {
      nextFolderType = FolderType.Mixed;
    } else if (rootFolderId && initialFolderType !== FolderType.Mixed) {
      nextFolderType = initialFolderType;
      nextSelectedMediaType = initialFolderType === FolderType.Ebook ? 'ebook' : 'audiobook';
    }

    this.props.onInputChange({ name: 'folderType', value: nextFolderType });

    if (nextSelectedMediaType !== selectedMediaType) {
      this.setState({ selectedMediaType: nextSelectedMediaType });
    }
  };

  getSaveButtonText = () => {
    const folderTypeValue = this.props.item?.folderType?.value;
    const rootFolderId = this.props.item?.id?.value;
    const verb = rootFolderId ? 'Update' : 'Save';

    if (folderTypeValue === FolderType.Mixed) {
      return `${verb} Mixed Folder`;
    }

    return this.state.selectedMediaType === 'audiobook' ?
      `${verb} Audiobook Folder` :
      `${verb} Ebook Folder`;
  };

  onSmartSavePress = () => {
    const folderTypeValue = this.props.item?.folderType?.value;
    const { hasViewedAudiobookTab, hasViewedEbookTab } = this.state;

    if (folderTypeValue === FolderType.Mixed && (!hasViewedAudiobookTab || !hasViewedEbookTab)) {
      if (hasViewedAudiobookTab) {
        this.setState({ selectedMediaType: 'ebook', hasViewedEbookTab: true });
      } else {
        this.setState({ selectedMediaType: 'audiobook', hasViewedAudiobookTab: true });
      }
      return;
    }

    this.props.onSavePress();
  };

  getSaveErrorMessage = () => {
    const { saveError } = this.props;

    if (saveError.message) {
      return saveError.message;
    }

    if (saveError.responseJSON && saveError.responseJSON.length > 0) {
      return saveError.responseJSON[0].errorMessage ||
        saveError.responseJSON[0].message ||
        'An error occurred while saving the root folder';
    }

    return 'An error occurred while saving the root folder';
  };

  render() {
    const {
      advancedSettings,
      isFetching,
      error,
      isSaving,
      saveError,
      item,
      onInputChange,
      onModalClose,
      onDeleteRootFolderPress,
      showMetadataProfile,
      isDefaultAudiobookRootFolder,
      isDefaultAudiobookRootFolderDisabled,
      isDefaultAudiobookRootFolderAutomatic,
      isDefaultEbookRootFolder,
      isDefaultEbookRootFolderDisabled,
      isDefaultEbookRootFolderAutomatic,
      onDefaultAudiobookRootFolderChange,
      onDefaultEbookRootFolderChange,
      ...otherProps
    } = this.props;

    const {
      name,
      path,
      folderType,
      placeEbooksWithAudiobooks,
      defaultSyncMonitoredAcrossFormats,
      isCalibreLibrary,
      host,
      port,
      urlBase,
      username,
      password,
      library,
      outputFormat,
      outputProfile,
      useSsl,
      audiobookMonitored,
      audiobookMonitorExistingMode,
      audiobookMonitorNewItems,
      ebookMonitored,
      ebookMonitorExistingMode,
      ebookMonitorNewItems,
      audiobookQualityProfileId,
      audiobookMetadataProfileId,
      ebookQualityProfileId,
      ebookMetadataProfileId,
      audiobookWriteAudioBookShelfMetadataJson,
      audiobookWriteAudioBookShelfCover,
      ebookWriteAudioBookShelfMetadataJson,
      ebookWriteAudioBookShelfCover,
      audiobookTags,
      ebookTags
    } = item || {};

    const rootFolderId = item?.id?.value;
    const isFolderTypeFixed = this.isFolderTypeFixed();
    const folderTypeValue = folderType?.value;
    const allowBothMediaTabs = !rootFolderId || folderTypeValue === FolderType.Mixed;
    const hasAudiobookTab = allowBothMediaTabs || folderTypeValue === FolderType.Audiobook;
    const hasEbookTab = allowBothMediaTabs || folderTypeValue === FolderType.Ebook;
    const supportsAudiobookDefaults = folderTypeValue === FolderType.Audiobook || folderTypeValue === FolderType.Mixed;
    const supportsEbookDefaults = folderTypeValue === FolderType.Ebook || folderTypeValue === FolderType.Mixed;

    const { selectedMediaType } = this.state;

    const outputProfileValue = outputProfile?.value ?? 'default';
    const profileHelpText = calibreProfiles.options.find((x) => x.key === outputProfileValue)?.description || '';

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {rootFolderId ? 'Edit Root Folder' : 'Add Root Folder'}
        </ModalHeader>

        <ModalBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && !!error &&
              <div>
                {translate('UnableToAddANewRootFolderPleaseTryAgain')}
              </div>
          }

          {
            !isFetching && !error &&
              <Form {...otherProps}>
                <FieldSet legend={translate('RootFolder')}>
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
                      {translate('Path')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.PATH}
                      name="path"
                      helpText={translate('PathHelpText')}
                      helpTextWarning={translate('PathHelpTextWarning')}
                      {...path}
                      onChange={onInputChange}
                    />
                  </FormGroup>

                  {
                    !isFolderTypeFixed &&
                      <FormGroup>
                        <FormLabel>
                          {translate('ImportMixedContent')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="folderType"
                          value={folderType?.value === FolderType.Mixed}
                          helpText={
                            folderType?.value === FolderType.Mixed ?
                              translate('ImportMixedContentEnabledHelpText') :
                              translate('ImportMixedContentDisabledHelpText')
                          }
                          onChange={this.onAcceptsMixedContentChange}
                        />
                      </FormGroup>
                  }

                  {
                    folderType?.value === FolderType.Mixed &&
                      <FormGroup>
                        <FormLabel>
                          {translate('PlaceEbooksWithAudiobooks')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="placeEbooksWithAudiobooks"
                          helpText={translate('PlaceEbooksWithAudiobooksHelpText')}
                          {...placeEbooksWithAudiobooks}
                          onChange={onInputChange}
                        />
                      </FormGroup>
                  }

                  {
                    supportsAudiobookDefaults &&
                      <FormGroup>
                        <FormLabel>
                          {translate('UseAsDefaultAudiobookRootFolder')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="isDefaultAudiobookRootFolder"
                          value={isDefaultAudiobookRootFolder}
                          isDisabled={isDefaultAudiobookRootFolderDisabled}
                          helpText={
                            isDefaultAudiobookRootFolderAutomatic ?
                              translate('UseAsDefaultAudiobookRootFolderAutomaticHelpText') :
                              translate('UseAsDefaultAudiobookRootFolderHelpText')
                          }
                          onChange={onDefaultAudiobookRootFolderChange}
                        />
                      </FormGroup>
                  }

                  {
                    supportsEbookDefaults &&
                      <FormGroup>
                        <FormLabel>
                          {translate('UseAsDefaultEbookRootFolder')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="isDefaultEbookRootFolder"
                          value={isDefaultEbookRootFolder}
                          isDisabled={isDefaultEbookRootFolderDisabled}
                          helpText={
                            isDefaultEbookRootFolderAutomatic ?
                              translate('UseAsDefaultEbookRootFolderAutomaticHelpText') :
                              translate('UseAsDefaultEbookRootFolderHelpText')
                          }
                          onChange={onDefaultEbookRootFolderChange}
                        />
                      </FormGroup>
                  }
                </FieldSet>

                <FieldSet legend={translate('AddedAuthorSettings')}>
                  <FormGroup>
                    <FormLabel>
                      {translate('SyncMonitoredAcrossFormatsByDefault')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="defaultSyncMonitoredAcrossFormats"
                      helpText={translate('SyncMonitoredAcrossFormatsByDefaultHelpText')}
                      {...(defaultSyncMonitoredAcrossFormats || { value: false })}
                      onChange={onInputChange}
                    />
                  </FormGroup>

                  {
                    !isFolderTypeFixed &&
                      <MediaTypeToggle
                        selectedMediaType={selectedMediaType}
                        onMediaTypeChange={this.onMediaTypeChange}
                        hasAudiobookRootFolder={hasAudiobookTab}
                        hasEbookRootFolder={hasEbookTab}
                      />
                  }

                  {selectedMediaType === 'audiobook' ? (
                    <div key="audiobook-settings">
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
                          {...(audiobookMonitored || { value: true })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('InitialBookMonitoring')}
                          <Popover
                            anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                            title={translate('InitialBookMonitoring')}
                            body={<AuthorMonitoringOptionsPopoverContent />}
                            position={tooltipPositions.RIGHT}
                          />
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.SELECT}
                          name="audiobookMonitorExistingMode"
                          values={monitorOptions}
                          {...(audiobookMonitorExistingMode || { value: 'all' })}
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
                          {...(audiobookMonitorNewItems || { value: 'none' })}
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
                          profileType="audiobook"
                          {...audiobookQualityProfileId}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup className={showMetadataProfile ? undefined : styles.hideMetadataProfile}>
                        <FormLabel>
                          {translate('AudiobookMetadataProfile')}
                          <Popover
                            anchor={
                              <Icon
                                className={styles.labelIcon}
                                name={icons.INFO}
                              />
                            }
                            title={translate('MetadataProfile')}
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
                          {translate('WriteAudioBookShelfMetadataJson')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="audiobookWriteAudioBookShelfMetadataJson"
                          helpText={translate('WriteAudioBookShelfMetadataJsonHelpText')}
                          {...(audiobookWriteAudioBookShelfMetadataJson || { value: false })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('WriteAudioBookShelfCover')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="audiobookWriteAudioBookShelfCover"
                          helpText={translate('WriteAudioBookShelfCoverHelpText')}
                          {...(audiobookWriteAudioBookShelfCover || { value: false })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('AudiobookTags')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.TAG}
                          name="audiobookTags"
                          helpText={translate('AudiobookTagsHelpText')}
                          {...audiobookTags}
                          onChange={onInputChange}
                        />
                      </FormGroup>
                    </div>
                  ) : (
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
                          {...(ebookMonitored || { value: true })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('InitialBookMonitoring')}
                          <Popover
                            anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                            title={translate('InitialBookMonitoring')}
                            body={<AuthorMonitoringOptionsPopoverContent />}
                            position={tooltipPositions.RIGHT}
                          />
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.SELECT}
                          name="ebookMonitorExistingMode"
                          values={monitorOptions}
                          {...(ebookMonitorExistingMode || { value: 'all' })}
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
                          {...(ebookMonitorNewItems || { value: 'none' })}
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
                          profileType="ebook"
                          {...ebookQualityProfileId}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup className={showMetadataProfile ? undefined : styles.hideMetadataProfile}>
                        <FormLabel>
                          {translate('EbookMetadataProfile')}
                          <Popover
                            anchor={
                              <Icon
                                className={styles.labelIcon}
                                name={icons.INFO}
                              />
                            }
                            title={translate('MetadataProfile')}
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
                          {translate('WriteAudioBookShelfMetadataJson')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="ebookWriteAudioBookShelfMetadataJson"
                          helpText={translate('WriteAudioBookShelfMetadataJsonHelpText')}
                          {...(ebookWriteAudioBookShelfMetadataJson || { value: false })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('WriteAudioBookShelfCover')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="ebookWriteAudioBookShelfCover"
                          helpText={translate('WriteAudioBookShelfCoverHelpText')}
                          {...(ebookWriteAudioBookShelfCover || { value: false })}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup>
                        <FormLabel>
                          {translate('EbookTags')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.TAG}
                          name="ebookTags"
                          helpText={translate('EbookTagsHelpText')}
                          {...ebookTags}
                          onChange={onInputChange}
                        />
                      </FormGroup>
                    </>
                  )}
                </FieldSet>

                {
                  SHOW_CALIBRE_UI &&
                    <FieldSet legend={translate('CalibreSettings')}>
                      <Alert>
                        {translate('CalibreNotCalibreWeb')}
                      </Alert>

                      <FormGroup>
                        <FormLabel>
                          {translate('UseCalibreContentServer')}
                          <Popover
                            anchor={
                              <Icon
                                className={styles.labelIcon}
                                name={icons.INFO}
                              />
                            }
                            title={translate('CalibreContentServer')}
                            body={translate('CalibreContentServerText')}
                            position={tooltipPositions.RIGHT}
                          />
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          isDisabled={Boolean(rootFolderId)}
                          name="isCalibreLibrary"
                          helpText={translate('IsCalibreLibraryHelpText')}
                          helpLink="https://manual.calibre-ebook.com/server.html"
                          {...isCalibreLibrary}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      {
                        isCalibreLibrary?.value &&
                          <div>
                            <FormGroup>
                              <FormLabel>
                                {translate('CalibreHost')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.TEXT}
                                name="host"
                                helpText={translate('CalibreHostHelpText')}
                                {...host}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('CalibrePort')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.NUMBER}
                                name="port"
                                helpText={translate('PortHelpText')}
                                {...port}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup
                              advancedSettings={advancedSettings}
                              isAdvanced={true}
                            >
                              <FormLabel>
                                {translate('CalibreUrlBase')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.TEXT}
                                name="urlBase"
                                helpText={translate('UrlBaseHelpText')}
                                {...urlBase}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('CalibreUsername')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.TEXT}
                                name="username"
                                helpText={translate('UsernameHelpText')}
                                {...username}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('CalibrePassword')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.PASSWORD}
                                name="password"
                                helpText={translate('PasswordHelpText')}
                                {...password}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('CalibreLibrary')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.TEXT}
                                name="library"
                                helpText={translate('LibraryHelpText')}
                                {...library}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('ConvertToFormat')}
                                <Popover
                                  anchor={
                                    <Icon
                                      className={styles.labelIcon}
                                      name={icons.INFO}
                                    />
                                  }
                                  title={translate('CalibreOutputFormat')}
                                  body="Specify the output format. Options are: MOBI, EPUB, AZW3, DOCX, FB2, HTMLZ, LIT, LRF, PDB, PDF, PMLZ, RB, RTF, SNB, TCR, TXT, TXTZ, ZIP"
                                  position={tooltipPositions.RIGHT}
                                />
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.TEXT}
                                name="outputFormat"
                                helpText={translate('OutputFormatHelpText')}
                                {...outputFormat}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('CalibreOutputProfile')}
                                <Popover
                                  anchor={
                                    <Icon
                                      className={styles.labelIcon}
                                      name={icons.INFO}
                                    />
                                  }
                                  title={translate('CalibreOutputProfile')}
                                  body="Specify the output profile. The output profile tells the Calibre conversion system how to optimize the created document for the specified device (such as by resizing images for the device screen size). In some cases, an output profile can be used to optimize the output for a particular device, but this is rarely necessary."
                                  position={tooltipPositions.RIGHT}
                                />
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.SELECT}
                                name="outputProfile"
                                values={calibreProfiles.options}
                                helpText={profileHelpText}
                                {...outputProfile}
                                onChange={onInputChange}
                              />
                            </FormGroup>

                            <FormGroup>
                              <FormLabel>
                                {translate('UseSSL')}
                              </FormLabel>

                              <FormInputGroup
                                type={inputTypes.CHECK}
                                name="useSsl"
                                helpText={translate('UseSslHelpText')}
                                {...useSsl}
                                onChange={onInputChange}
                              />
                            </FormGroup>
                          </div>
                      }
                    </FieldSet>
                }
              </Form>
          }
        </ModalBody>

        <ModalFooter>
          {
            saveError &&
              <Alert kind={kinds.DANGER} className={styles.saveError}>
                {this.getSaveErrorMessage()}
                {
                  saveError.message && saveError.message.toLowerCase().includes('permission') &&
                    ' Please check that the Docker container has read permissions for this directory.'
                }
              </Alert>
          }

          {
            rootFolderId &&
              <Button
                className={styles.deleteButton}
                kind={kinds.DANGER}
                onPress={onDeleteRootFolderPress}
              >
                {translate('Delete')}
              </Button>
          }

          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>

          <SpinnerErrorButton
            isSpinning={isSaving}
            error={saveError}
            onPress={this.onSmartSavePress}
          >
            {this.getSaveButtonText()}
          </SpinnerErrorButton>
        </ModalFooter>
      </ModalContent>
    );
  }
}

EditRootFolderModalContent.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  showMetadataProfile: PropTypes.bool.isRequired,
  folderTypeProp: PropTypes.number,
  isDefaultAudiobookRootFolder: PropTypes.bool.isRequired,
  isDefaultAudiobookRootFolderDisabled: PropTypes.bool.isRequired,
  isDefaultAudiobookRootFolderAutomatic: PropTypes.bool.isRequired,
  isDefaultEbookRootFolder: PropTypes.bool.isRequired,
  isDefaultEbookRootFolderDisabled: PropTypes.bool.isRequired,
  isDefaultEbookRootFolderAutomatic: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onDefaultAudiobookRootFolderChange: PropTypes.func.isRequired,
  onDefaultEbookRootFolderChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onDeleteRootFolderPress: PropTypes.func
};

export default EditRootFolderModalContent;
