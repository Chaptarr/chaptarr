import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';

const bitrateOptions = [
  { key: 32, value: '32 kbps' },
  { key: 48, value: '48 kbps' },
  { key: 64, value: '64 kbps' },
  { key: 80, value: '80 kbps' },
  { key: 96, value: '96 kbps' },
  { key: 128, value: '128 kbps' },
  { key: 160, value: '160 kbps' },
  { key: 192, value: '192 kbps' },
  { key: 256, value: '256 kbps' },
  { key: 320, value: '320 kbps' }
];

function getAudioChannelOptions() {
  return [
    { key: 'source', value: translate('KeepSourceChannels') },
    { key: 'mono', value: translate('ConvertToMono') }
  ];
}

function getTagModeOptions() {
  return [
    { key: 'preserve', value: translate('PreserveSourceTags') },
    { key: 'clean', value: translate('CleanMatchedTags') }
  ];
}

class ConversionSettings extends Component {

  //
  // Render

  render() {
    const {
      isFetching,
      error,
      settings,
      hasSettings,
      advancedSettings,
      onInputChange,
      onSavePress,
      onRetryPress,
      ...otherProps
    } = this.props;

    return (
      <PageContent title={translate('ConversionSettings')}>
        <SettingsToolbarConnector
          advancedSettings={advancedSettings}
          {...otherProps}
          onSavePress={onSavePress}
        />

        <PageContentBody>
          {
            isFetching &&
              <FieldSet legend={translate('Conversion')}>
                <LoadingIndicator />
              </FieldSet>
          }

          {
            !isFetching && error &&
              <FieldSet legend={translate('Conversion')}>
                <Alert kind={kinds.DANGER} role="alert">
                  <div>{getErrorMessage(error, translate('UnableToLoadConversionSettings'))}</div>
                  <Button size="small" onPress={onRetryPress}>
                    {translate('Retry')}
                  </Button>
                </Alert>
              </FieldSet>
          }

          {
            hasSettings && !isFetching && !error &&
              <Form
                id="conversionSettings"
                {...otherProps}
              >
                <FieldSet legend={translate('AudiobookConversion')}>
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('MaxCpuThreads')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.NUMBER}
                      name="audiobookConversionMaxCpuThreads"
                      min={1}
                      max={64}
                      helpText={translate('AudiobookConversionMaxCpuThreadsHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionMaxCpuThreads}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>
                      {translate('ConcurrentConversions')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.NUMBER}
                      name="audiobookConversionConcurrentConversions"
                      min={1}
                      max={16}
                      helpText={translate('AudiobookConversionConcurrentConversionsHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionConcurrentConversions}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('PreferredBitrate')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="audiobookConversionMaxBitrate"
                      values={bitrateOptions}
                      helpText={translate('AudiobookConversionMaxBitrateHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionMaxBitrate}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('DoNotUpscaleBitrate')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="audiobookConversionNoUpscale"
                      helpText={translate('AudiobookConversionNoUpscaleHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionNoUpscale}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>
                      {translate('ConversionTags')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="audiobookConversionTagMode"
                      values={getTagModeOptions()}
                      helpText={translate('AudiobookConversionTagModeHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionTagMode}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>
                      {translate('AudioChannels')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="audiobookConversionAudioChannels"
                      values={getAudioChannelOptions()}
                      helpText={translate('AudiobookConversionAudioChannelsHelpText')}
                      onChange={onInputChange}
                      {...settings.audiobookConversionAudioChannels}
                    />
                  </FormGroup>
                </FieldSet>

              </Form>
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

ConversionSettings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onRetryPress: PropTypes.func.isRequired,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  advancedSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired
};

export default ConversionSettings;
