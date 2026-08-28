import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorMonitoringGatePopoverContent from 'AddAuthor/AuthorMonitoringGatePopoverContent';
import AuthorMonitoringOptionsPopoverContent from 'AddAuthor/AuthorMonitoringOptionsPopoverContent';
import AuthorMonitorNewItemsOptionsPopoverContent from 'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent';
import BookMonitoringOptionsPopoverContent from 'AddAuthor/BookMonitoringOptionsPopoverContent';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import Popover from 'Components/Tooltip/Popover';
import { icons, inputTypes, kinds, sizes, tooltipPositions } from 'Helpers/Props';
import monitorNewItemsOptions, { resolveMonitorNewItemsOptionValue } from 'Utilities/Author/monitorNewItemsOptions';
import monitorOptions, { resolveMonitorOptionValue } from 'Utilities/Author/monitorOptions';
import translate from 'Utilities/String/translate';
import styles from './AddAuthorOptionsForm.css';

const specificBookMonitorOptions = [
  { key: 'all', value: translate('AllBooks') },
  { key: 'specificBook', value: translate('OnlyThisBook') }
];

class AddAuthorOptionsForm extends Component {

  //
  // Listeners

  onMediaTypeChange = (mediaType) => {
    this.props.onMediaTypeChange(mediaType);
  };

  onSetDefaultMediaTypePress = () => {
    const { selectedMediaType, onSetDefaultMediaType } = this.props;
    if (onSetDefaultMediaType) {
      onSetDefaultMediaType(selectedMediaType);
    }
  };

  onAudiobookQualityProfileIdChange = ({ value }) => {
    this.props.onInputChange({ name: 'audiobookQualityProfileId', value });
  };

  onEbookQualityProfileIdChange = ({ value }) => {
    this.props.onInputChange({ name: 'ebookQualityProfileId', value });
  };

  onAudiobookMetadataProfileIdChange = ({ value }) => {
    this.props.onInputChange({ name: 'audiobookMetadataProfileId', value });
  };

  onEbookMetadataProfileIdChange = ({ value }) => {
    this.props.onInputChange({ name: 'ebookMetadataProfileId', value });
  };

  //
  // Render

  render() {
    const {
      audiobookRootFolderPath,
      ebookRootFolderPath,
      monitor,
      audiobookMonitored,
      ebookMonitored,
      audiobookMonitor,
      ebookMonitor,
      monitorNewItems,
      audiobookMonitorNewItems,
      ebookMonitorNewItems,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      includeNoneMetadataProfile,
      includeSpecificBookMonitor,
      includeBothMediaType,
      defaultMediaType,
      onSetDefaultMediaType,
      isSavingDefaultMediaType,
      selectedMediaType,
      showMetadataProfile,
      folder,
      tags,
      audiobookTags,
      ebookTags,
      isWindows,
      isExistingAuthor,
      onInputChange,
      ...otherProps
    } = this.props;

    const normalizedDefaultMediaType = (defaultMediaType ?? '').trim().toLowerCase();
    const normalizedSelectedMediaType = (selectedMediaType ?? '').trim().toLowerCase();
    const isOnDefault = normalizedDefaultMediaType && normalizedSelectedMediaType === normalizedDefaultMediaType;
    const showSetDefaultButton = !!onSetDefaultMediaType && (!normalizedDefaultMediaType || !isOnDefault);

    // Root folder option availability is resolved inside RootFolderSelectInputConnector.
    // Keep the media-type toggle enabled so users can switch media types and configure settings.
    const hasAudiobookRootFolder = true;
    const hasEbookRootFolder = true;

    return (
      <Form {...otherProps}>
        <MediaTypeToggle
          selectedMediaType={selectedMediaType}
          onMediaTypeChange={this.onMediaTypeChange}
          hasAudiobookRootFolder={hasAudiobookRootFolder}
          hasEbookRootFolder={hasEbookRootFolder}
          includeBoth={includeBothMediaType}
        >
          {
            showSetDefaultButton ?
              <Button
                kind={kinds.PRIMARY}
                size={sizes.SMALL}
                isDisabled={isSavingDefaultMediaType}
                onPress={this.onSetDefaultMediaTypePress}
              >
                {translate('SetAsDefault')}
              </Button> :
              null
          }
        </MediaTypeToggle>

        {(() => {
          const audiobookSettings = (
            <>
              <FormGroup>
                <FormLabel>
                  {translate('AudiobookRootFolder')}
                </FormLabel>

                {isExistingAuthor ? (
                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="audiobookRootFolderPath_display"
                    value={audiobookRootFolderPath?.value || folder || ''}
                    helpText={translate('AddAuthorUsingExistingFolder')}
                    readOnly={true}
                    onChange={() => {}}
                  />
                ) : (
                  <FormInputGroup
                    type={inputTypes.ROOT_FOLDER_SELECT}
                    name="audiobookRootFolderPath"
                    valueOptions={{
                      authorFolder: folder,
                      isWindows
                    }}
                    selectedValueOptions={{
                      authorFolder: folder,
                      isWindows
                    }}
                    helpText={translate('AddAuthorAudiobookRootFolderHelpText')}
                    folderType={1}
                    onChange={onInputChange}
                    {...audiobookRootFolderPath}
                  />
                )}
              </FormGroup>

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
                  value={audiobookMonitored?.value !== false}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('InitialBookMonitoring')}
                  <Popover
                    anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                    title={translate('InitialBookMonitoring')}
                    body={includeSpecificBookMonitor ? <BookMonitoringOptionsPopoverContent /> : <AuthorMonitoringOptionsPopoverContent />}
                    position={tooltipPositions.RIGHT}
                  />
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="audiobookMonitor"
                  {...(audiobookMonitor || monitor)}
                  values={includeSpecificBookMonitor ? specificBookMonitorOptions : monitorOptions}
                  value={(() => {
                    const currentValue = resolveMonitorOptionValue(audiobookMonitor?.value, monitor?.value);

                    if (includeSpecificBookMonitor) {
                      return currentValue === 'all' ? 'all' : 'specificBook';
                    }

                    return currentValue;
                  })()}
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
                  {...(audiobookMonitorNewItems || monitorNewItems)}
                  value={resolveMonitorNewItemsOptionValue(audiobookMonitorNewItems?.value, monitorNewItems?.value)}
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
                  includeNone={false}
                  profileType="audiobook"
                  onChange={this.onAudiobookQualityProfileIdChange}
                  {...audiobookQualityProfileId}
                />
              </FormGroup>

              {
                showMetadataProfile ?
                  <FormGroup>
                    <FormLabel>
                      {translate('AudiobookMetadataProfile')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.METADATA_PROFILE_SELECT}
                      name="audiobookMetadataProfileId"
                      helpText={translate('AudiobookMetadataProfileHelpText')}
                      includeNone={includeNoneMetadataProfile}
                      profileType="audiobook"
                      onChange={this.onAudiobookMetadataProfileIdChange}
                      {...audiobookMetadataProfileId}
                    />
                  </FormGroup> :
                  null
              }

              <FormGroup>
                <FormLabel>
                  {translate('Tags')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TAG}
                  name="audiobookTags"
                  onChange={onInputChange}
                  {...(audiobookTags || tags)}
                />
              </FormGroup>
            </>
          );

          const ebookSettings = (
            <>
              <FormGroup>
                <FormLabel>
                  {translate('EbookRootFolder')}
                </FormLabel>

                {isExistingAuthor ? (
                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="ebookRootFolderPath_display"
                    value={ebookRootFolderPath?.value || folder || ''}
                    helpText={translate('AddAuthorUsingExistingFolder')}
                    readOnly={true}
                    onChange={() => {}}
                  />
                ) : (
                  <FormInputGroup
                    type={inputTypes.ROOT_FOLDER_SELECT}
                    name="ebookRootFolderPath"
                    valueOptions={{
                      authorFolder: folder,
                      isWindows
                    }}
                    selectedValueOptions={{
                      authorFolder: folder,
                      isWindows
                    }}
                    helpText={translate('AddAuthorEbookRootFolderHelpText')}
                    folderType={2}
                    onChange={onInputChange}
                    {...ebookRootFolderPath}
                  />
                )}
              </FormGroup>

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
                  value={ebookMonitored?.value !== false}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('InitialBookMonitoring')}
                  <Popover
                    anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
                    title={translate('InitialBookMonitoring')}
                    body={includeSpecificBookMonitor ? <BookMonitoringOptionsPopoverContent /> : <AuthorMonitoringOptionsPopoverContent />}
                    position={tooltipPositions.RIGHT}
                  />
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="ebookMonitor"
                  {...(ebookMonitor || monitor)}
                  values={includeSpecificBookMonitor ? specificBookMonitorOptions : monitorOptions}
                  value={(() => {
                    const currentValue = resolveMonitorOptionValue(ebookMonitor?.value, monitor?.value);

                    if (includeSpecificBookMonitor) {
                      return currentValue === 'all' ? 'all' : 'specificBook';
                    }

                    return currentValue;
                  })()}
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
                  {...(ebookMonitorNewItems || monitorNewItems)}
                  value={resolveMonitorNewItemsOptionValue(ebookMonitorNewItems?.value, monitorNewItems?.value)}
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
                  includeNone={false}
                  profileType="ebook"
                  onChange={this.onEbookQualityProfileIdChange}
                  {...ebookQualityProfileId}
                />
              </FormGroup>

              {
                showMetadataProfile ?
                  <FormGroup>
                    <FormLabel>
                      {translate('EbookMetadataProfile')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.METADATA_PROFILE_SELECT}
                      name="ebookMetadataProfileId"
                      helpText={translate('EbookMetadataProfileHelpText')}
                      includeNone={includeNoneMetadataProfile}
                      profileType="ebook"
                      onChange={this.onEbookMetadataProfileIdChange}
                      {...ebookMetadataProfileId}
                    />
                  </FormGroup> :
                  null
              }

              <FormGroup>
                <FormLabel>
                  {translate('Tags')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TAG}
                  name="ebookTags"
                  onChange={onInputChange}
                  {...(ebookTags || tags)}
                />
              </FormGroup>
            </>
          );

          if (selectedMediaType === 'audiobook') {
            return audiobookSettings;
          }

          if (selectedMediaType === 'ebook') {
            return ebookSettings;
          }

          // Both
          return (
            <>
              {audiobookSettings}
              {ebookSettings}
            </>
          );
        })()}
      </Form>
    );
  }
}

AddAuthorOptionsForm.propTypes = {
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  monitor: PropTypes.object.isRequired,
  audiobookMonitored: PropTypes.object,
  ebookMonitored: PropTypes.object,
  audiobookMonitor: PropTypes.object,
  ebookMonitor: PropTypes.object,
  monitorNewItems: PropTypes.object.isRequired,
  audiobookMonitorNewItems: PropTypes.object,
  ebookMonitorNewItems: PropTypes.object,
  audiobookQualityProfileId: PropTypes.object,
  ebookQualityProfileId: PropTypes.object,
  metadataProfileId: PropTypes.object,
  audiobookMetadataProfileId: PropTypes.object,
  ebookMetadataProfileId: PropTypes.object,
  showMetadataProfile: PropTypes.bool.isRequired,
  includeNoneMetadataProfile: PropTypes.bool.isRequired,
  includeSpecificBookMonitor: PropTypes.bool.isRequired,
  includeBothMediaType: PropTypes.bool,
  defaultMediaType: PropTypes.string,
  onSetDefaultMediaType: PropTypes.func,
  isSavingDefaultMediaType: PropTypes.bool,
  folder: PropTypes.string.isRequired,
  tags: PropTypes.object.isRequired,
  audiobookTags: PropTypes.object,
  ebookTags: PropTypes.object,
  isWindows: PropTypes.bool.isRequired,
  isExistingAuthor: PropTypes.bool,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired
};

AddAuthorOptionsForm.defaultProps = {
  includeSpecificBookMonitor: false,
  includeBothMediaType: false
};

export default AddAuthorOptionsForm;
