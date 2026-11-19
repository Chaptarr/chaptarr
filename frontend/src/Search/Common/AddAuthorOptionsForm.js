import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorMonitoringGatePopoverContent from 'AddAuthor/AuthorMonitoringGatePopoverContent';
import AuthorMonitoringOptionsPopoverContent from 'AddAuthor/AuthorMonitoringOptionsPopoverContent';
import AuthorMonitorNewItemsOptionsPopoverContent from 'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent';
import BookMonitoringOptionsPopoverContent from 'AddAuthor/BookMonitoringOptionsPopoverContent';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
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
      includeSyncMonitoredAcrossFormats,
      syncMonitoredAcrossFormats,
      canSyncMonitoredAcrossFormats,
      sharedMonitor,
      sharedMonitorNewItems,
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
      existingAuthor,
      qualityProfiles,
      metadataProfiles,
      tagList,
      onInputChange,
      ...otherProps
    } = this.props;

    const renderExistingSettings = (mediaType) => {
      const selectedMonitor = mediaType === 'audiobook' ? audiobookMonitor : ebookMonitor;
      const fields = [
        ['RootFolder', existingAuthor[`${mediaType}RootFolderPath`]],
        ['QualityProfile', qualityProfiles?.find((profile) => profile.id === existingAuthor[`${mediaType}QualityProfileId`])?.name],
        ['MetadataProfile', metadataProfiles?.find((profile) => profile.id === existingAuthor[`${mediaType}MetadataProfileId`])?.name],
        ['MonitorAuthor', translate(existingAuthor[`${mediaType}Monitored`] ? 'Yes' : 'No')],
        ['MonitorNewBooks', monitorNewItemsOptions.find((option) => option.key === existingAuthor[`${mediaType}MonitorNewItems`])?.value],
        ['Tags', (tagList || []).filter((tag) => (existingAuthor[`${mediaType}Tags`] || []).includes(tag.id)).map((tag) => tag.label).join(', ')]
      ];
      return (
        <>
          <FormGroup>
            <FormLabel>{translate(mediaType === 'audiobook' ? 'Audiobooks' : 'Ebooks')}</FormLabel>
            <div>
              <DescriptionList>
                {fields.map(([label, value]) => (
                  <DescriptionListItem
                    key={label}
                    title={translate(label)}
                    data={value || translate('None')}
                  />
                ))}
              </DescriptionList>
              <p>{translate('AddBookUsesExistingAuthorSettings')}</p>
            </div>
          </FormGroup>
          <FormGroup>
            <FormLabel>{translate('MonitorThisBook')}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name={`${mediaType}MonitorThisBook`}
              value={selectedMonitor?.value !== 'none'}
              onChange={({ value }) => onInputChange({ name: `${mediaType}Monitor`, value: value ? 'specificBook' : 'none' })}
            />
          </FormGroup>
        </>
      );
    };

    const normalizedDefaultMediaType = (defaultMediaType ?? '').trim().toLowerCase();
    const normalizedSelectedMediaType = (selectedMediaType ?? '').trim().toLowerCase();
    const isOnDefault = normalizedDefaultMediaType && normalizedSelectedMediaType === normalizedDefaultMediaType;
    const showSetDefaultButton = !!onSetDefaultMediaType && (!normalizedDefaultMediaType || !isOnDefault);

    // Root folder option availability is resolved inside RootFolderSelectInputConnector.
    // Keep the media-type toggle enabled so users can switch media types and configure settings.
    const hasAudiobookRootFolder = true;
    const hasEbookRootFolder = true;

    const isSyncEnabled = includeSyncMonitoredAcrossFormats && syncMonitoredAcrossFormats?.value === true;
    const renderMonitoringOptions = (mediaType) => {
      const shared = mediaType === 'shared';
      const initial = shared ? sharedMonitor : (mediaType === 'ebook' ? ebookMonitor : audiobookMonitor) || monitor;
      const ongoing = shared ? sharedMonitorNewItems : (mediaType === 'ebook' ? ebookMonitorNewItems : audiobookMonitorNewItems) || monitorNewItems;
      let initialValue = shared ? initial.value : resolveMonitorOptionValue(initial?.value, monitor?.value);
      if (includeSpecificBookMonitor) {
        initialValue = initialValue === 'all' ? 'all' : 'specificBook';
      }
      const unresolvedOption = { key: '', value: translate('Select'), isDisabled: true };
      const initialOptions = includeSpecificBookMonitor ? specificBookMonitorOptions : monitorOptions;

      return (
        <>
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
              name={`${mediaType}Monitor`}
              {...initial}
              values={shared && !initial.value ? [unresolvedOption, ...initialOptions] : initialOptions}
              value={initialValue}
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
              name={`${mediaType}MonitorNewItems`}
              {...ongoing}
              values={shared && !ongoing.value ? [unresolvedOption, ...monitorNewItemsOptions] : monitorNewItemsOptions}
              value={shared ? ongoing.value : resolveMonitorNewItemsOptionValue(ongoing?.value, monitorNewItems?.value)}
              onChange={onInputChange}
            />
          </FormGroup>
        </>
      );
    };

    return (
      <Form {...otherProps}>
        {
          includeSyncMonitoredAcrossFormats &&
            <FormGroup>
              <FormLabel>{translate('SyncMonitoredAudioEbooks')}</FormLabel>
              <FormInputGroup
                type={inputTypes.CHECK}
                name="syncMonitoredAcrossFormats"
                {...syncMonitoredAcrossFormats}
                isDisabled={!canSyncMonitoredAcrossFormats}
                helpText={canSyncMonitoredAcrossFormats ?
                  translate('AuthorSyncAddHelpText') :
                  translate('AuthorSyncAddRequiresBoth')}
                onChange={onInputChange}
              />
            </FormGroup>
        }
        {isSyncEnabled && renderMonitoringOptions('shared')}
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
          const audiobookSettings = includeSpecificBookMonitor && existingAuthor?.audiobookRootFolderPath ? renderExistingSettings('audiobook') : (
            <>
              <FormGroup>
                <FormLabel>
                  {translate('AudiobookRootFolder')}
                </FormLabel>

                {existingAuthor?.audiobookRootFolderPath ? (
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

              {!isSyncEnabled && renderMonitoringOptions('audiobook')}

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

          const ebookSettings = includeSpecificBookMonitor && existingAuthor?.ebookRootFolderPath ? renderExistingSettings('ebook') : (
            <>
              <FormGroup>
                <FormLabel>
                  {translate('EbookRootFolder')}
                </FormLabel>

                {existingAuthor?.ebookRootFolderPath ? (
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

              {!isSyncEnabled && renderMonitoringOptions('ebook')}

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
  includeSyncMonitoredAcrossFormats: PropTypes.bool,
  syncMonitoredAcrossFormats: PropTypes.object,
  canSyncMonitoredAcrossFormats: PropTypes.bool,
  sharedMonitor: PropTypes.object,
  sharedMonitorNewItems: PropTypes.object,
  defaultMediaType: PropTypes.string,
  onSetDefaultMediaType: PropTypes.func,
  isSavingDefaultMediaType: PropTypes.bool,
  folder: PropTypes.string.isRequired,
  tags: PropTypes.object.isRequired,
  audiobookTags: PropTypes.object,
  ebookTags: PropTypes.object,
  isWindows: PropTypes.bool.isRequired,
  existingAuthor: PropTypes.object,
  qualityProfiles: PropTypes.arrayOf(PropTypes.object),
  metadataProfiles: PropTypes.arrayOf(PropTypes.object),
  tagList: PropTypes.arrayOf(PropTypes.object),
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired
};

AddAuthorOptionsForm.defaultProps = {
  includeSpecificBookMonitor: false,
  includeBothMediaType: false
};

export default AddAuthorOptionsForm;
