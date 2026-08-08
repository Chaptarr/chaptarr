import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import Button from 'Components/Link/Button';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

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
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="audiobookMonitor"
                  helpText={translate('AddAuthorWhichAudiobooksToMonitor')}
                  {...(audiobookMonitor || monitor)}
                  values={includeSpecificBookMonitor ? [
                    { key: 'all', value: translate('AllBooks') },
                    { key: 'specificBook', value: translate('OnlyThisBook') }
                  ] : [
                    { key: 'all', value: translate('AllBooks') },
                    { key: 'select', value: translate('SelectBooks') },
                    { key: 'none', value: translate('None') }
                  ]}
                  value={(() => {
                    const currentValue = (audiobookMonitor || monitor)?.value;

                    if (includeSpecificBookMonitor) {
                      return currentValue === 'specificBook' ? 'specificBook' : 'all';
                    }

                    if (currentValue === 'specificBook') {
                      return 'select';
                    }

                    if (currentValue === 'all' || currentValue === 'select' || currentValue === 'none') {
                      return currentValue;
                    }

                    return 'all';
                  })()}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('MonitorNewBooks')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="audiobookMonitorNewItems"
                  helpText={translate('AddAuthorMonitorNewAudiobooksHelpText')}
                  {...(audiobookMonitorNewItems || monitorNewItems)}
                  value={(audiobookMonitorNewItems || monitorNewItems)?.value === 'all'}
                  onChange={({ value }) => onInputChange({
                    name: 'audiobookMonitorNewItems',
                    value: value ? 'all' : 'none'
                  })}
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
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="ebookMonitor"
                  helpText={translate('AddAuthorWhichEbooksToMonitor')}
                  {...(ebookMonitor || monitor)}
                  values={includeSpecificBookMonitor ? [
                    { key: 'all', value: translate('AllBooks') },
                    { key: 'specificBook', value: translate('OnlyThisBook') }
                  ] : [
                    { key: 'all', value: translate('AllBooks') },
                    { key: 'select', value: translate('SelectBooks') },
                    { key: 'none', value: translate('None') }
                  ]}
                  value={(() => {
                    const currentValue = (ebookMonitor || monitor)?.value;

                    if (includeSpecificBookMonitor) {
                      return currentValue === 'specificBook' ? 'specificBook' : 'all';
                    }

                    if (currentValue === 'specificBook') {
                      return 'select';
                    }

                    if (currentValue === 'all' || currentValue === 'select' || currentValue === 'none') {
                      return currentValue;
                    }

                    return 'all';
                  })()}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('MonitorNewBooks')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="ebookMonitorNewItems"
                  helpText={translate('AddAuthorMonitorNewEbooksHelpText')}
                  {...(ebookMonitorNewItems || monitorNewItems)}
                  value={(ebookMonitorNewItems || monitorNewItems)?.value === 'all'}
                  onChange={({ value }) => onInputChange({
                    name: 'ebookMonitorNewItems',
                    value: value ? 'all' : 'none'
                  })}
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
