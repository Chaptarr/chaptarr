import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Measure from 'Components/Measure';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, inputTypes, kinds, sizes, tooltipPositions } from 'Helpers/Props';
import AdvancedSettingsButton from 'Settings/AdvancedSettingsButton';
import dimensions from 'Styles/Variables/dimensions';
import translate from 'Utilities/String/translate';
import {
  easyCustomFormatPresetOptions
} from './dramatizedAudioPreference';
import QualityProfileFormatItems from './QualityProfileFormatItems';
import QualityProfileItems from './QualityProfileItems';
import styles from './EditQualityProfileModalContent.css';

const MODAL_BODY_PADDING = parseInt(dimensions.modalBodyPadding);

const releasePriorityOptions = [
  {
    key: 'preferences',
    get value() {
      return translate('PreferencesBeforeFileFormatRecommended');
    }
  },
  {
    key: 'fileFormat',
    get value() {
      return translate('FileFormatBeforePreferencesTraditionalArr');
    }
  }
];

function fieldWithDefault(field, value) {
  return {
    ...field,
    value: field?.value ?? value,
    errors: field?.errors || [],
    warnings: field?.warnings || []
  };
}

function arrayFieldWithDefault(field) {
  const fieldWithValue = fieldWithDefault(field, []);

  return {
    ...fieldWithValue,
    value: fieldWithValue.value || [],
    errors: fieldWithValue.errors || [],
    warnings: fieldWithValue.warnings || []
  };
}

function getCustomFormatRender(formatItems, advancedSettings, otherProps) {
  const profileFormatItems = formatItems || {
    value: [],
    errors: [],
    warnings: []
  };

  const profileFormatItemsValue = profileFormatItems.value || [];

  return (
    <QualityProfileFormatItems
      profileFormatItems={profileFormatItemsValue}
      errors={profileFormatItems.errors || []}
      warnings={profileFormatItems.warnings || []}
      advancedSettings={advancedSettings}
      isAdvanced={false}
      {...otherProps}
    />
  );
}

class EditQualityProfileModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      headerHeight: 0,
      bodyHeight: 0,
      footerHeight: 0
    };
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      headerHeight,
      bodyHeight,
      footerHeight
    } = this.state;

    if (
      headerHeight > 0 &&
      bodyHeight > 0 &&
      footerHeight > 0 &&
      (
        headerHeight !== prevState.headerHeight ||
        bodyHeight !== prevState.bodyHeight ||
        footerHeight !== prevState.footerHeight
      )
    ) {
      const padding = MODAL_BODY_PADDING * 2;

      this.props.onContentHeightChange(
        headerHeight + bodyHeight + footerHeight + padding
      );
    }
  }

  //
  // Listeners

  onHeaderMeasure = ({ height }) => {
    if (height > this.state.headerHeight) {
      this.setState({ headerHeight: height });
    }
  };

  onBodyMeasure = ({ height }) => {

    if (height > this.state.bodyHeight) {
      this.setState({ bodyHeight: height });
    }
  };

  onFooterMeasure = ({ height }) => {
    if (height > this.state.footerHeight) {
      this.setState({ footerHeight: height });
    }
  };

  //
  // Render

  render() {
    const {
      editGroups,
      advancedSettings,
      isFetching,
      error,
      isSaving,
      saveError,
      qualities,
      convertToQualities,
      item,
      isInUse,
      onInputChange,
      onCutoffChange,
      onConvertToQualityChange,
      onReleasePriorityChange,
      onEasyCustomFormatPresetChange,
      onAdvancedSettingsPress,
      onSavePress,
      onModalClose,
      onDeleteQualityProfilePress,
      ...otherProps
    } = this.props;

    const {
      id,
      name,
      profileType,
      upgradeAllowed,
      preferCustomFormatsOverQuality,
      convertToQualityId,
      cutoff,
      minFormatScore,
      cutoffFormatScore,
      items,
      formatItems
    } = item;
    const nameField = fieldWithDefault(name, '');
    const upgradeAllowedField = fieldWithDefault(upgradeAllowed, false);
    const preferCustomFormatsOverQualityField = fieldWithDefault(preferCustomFormatsOverQuality, false);
    const convertToQualityIdField = fieldWithDefault(convertToQualityId, 0);
    const cutoffField = fieldWithDefault(cutoff, 0);
    const minFormatScoreField = fieldWithDefault(minFormatScore, 0);
    const cutoffFormatScoreField = fieldWithDefault(cutoffFormatScore, 0);
    const itemsField = arrayFieldWithDefault(items);
    const formatItemsField = arrayFieldWithDefault(formatItems);
    const profileTypeValue = profileType?.value;
    const itemsValue = itemsField.value;
    const formatItemsValue = formatItemsField.value;
    const isAudiobookProfile = profileTypeValue === 'audiobook' || profileTypeValue === 1;
    const releasePriorityHelpText = preferCustomFormatsOverQualityField.value ?
      translate('PreferencesBeforeFileFormatHelpText') :
      translate('FileFormatBeforePreferencesHelpText');
    const deleteDisabledTooltip = translate('IsInUseCantDeleteAQualityProfileThatIsAttachedToAnAuthorImportListOrRootFolder');
    let deleteButton = null;

    if (id && isInUse) {
      deleteButton = (
        <Tooltip
          className={styles.deleteButtonContainer}
          position={tooltipPositions.TOP}
          anchor={
            <span className={styles.deleteButtonWithReason}>
              <Button
                kind={kinds.DANGER}
                isDisabled={true}
                onPress={onDeleteQualityProfilePress}
              >
                {translate('Delete')}
              </Button>

              <Icon
                className={styles.deleteDisabledReasonIcon}
                name={icons.INFO}
              />
            </span>
          }
          tooltip={deleteDisabledTooltip}
        />
      );
    } else if (id) {
      deleteButton = (
        <div className={styles.deleteButtonContainer}>
          <Button
            kind={kinds.DANGER}
            onPress={onDeleteQualityProfilePress}
          >
            {translate('Delete')}
          </Button>
        </div>
      );
    }

    return (
      <ModalContent onModalClose={onModalClose}>
        <Measure
          onMeasure={this.onHeaderMeasure}
        >
          <ModalHeader>
            {id ? translate('EditQualityProfile') : translate('AddQualityProfile')}
          </ModalHeader>
        </Measure>

        <ModalBody>
          <Measure
            onMeasure={this.onBodyMeasure}
          >
            {
              isFetching &&
                <LoadingIndicator />
            }

            {
              !isFetching && !!error &&
                <div>
                  {translate('UnableToAddANewQualityProfilePleaseTryAgain')}
                </div>
            }

            {
              !isFetching && !error &&
                <Form {...otherProps}>
                  <div className={styles.formGroupsContainer}>
                    <div className={styles.formGroupWrapper}>
                      <FormGroup size={sizes.EXTRA_SMALL}>
                        <FormLabel size={sizes.SMALL}>
                          {translate('Name')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.TEXT}
                          name="name"
                          {...nameField}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      <FormGroup size={sizes.EXTRA_SMALL}>
                        <FormLabel size={sizes.SMALL}>
                          {translate('UpgradesAllowed')}
                        </FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="upgradeAllowed"
                          {...upgradeAllowedField}
                          helpText={translate('UpgradeAllowedHelpText')}
                          onChange={onInputChange}
                        />
                      </FormGroup>

                      {
                        isAudiobookProfile &&
                          <FormGroup size={sizes.EXTRA_SMALL}>
                            <FormLabel size={sizes.SMALL}>
                              {translate('ReleasePriority')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.SELECT}
                              name="releasePriority"
                              value={preferCustomFormatsOverQualityField.value ? 'preferences' : 'fileFormat'}
                              values={releasePriorityOptions}
                              helpText={releasePriorityHelpText}
                              onChange={onReleasePriorityChange}
                            />
                          </FormGroup>
                      }

                      {
                        isAudiobookProfile &&
                          <FormGroup size={sizes.EXTRA_SMALL}>
                            <FormLabel size={sizes.SMALL}>
                              {translate('ConvertToQuality')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.SELECT}
                              name="convertToQualityId"
                              {...convertToQualityIdField}
                              value={convertToQualityIdField.value || 0}
                              values={convertToQualities}
                              helpText={Number(convertToQualityIdField.value) > 0 ?
                                translate('ConvertToQualityHelpText') :
                                undefined}
                              onChange={onConvertToQualityChange}
                            />
                          </FormGroup>
                      }

                      {
                        isAudiobookProfile && Number(convertToQualityIdField.value) > 0 &&
                          <FormGroup
                            size={sizes.EXTRA_SMALL}
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                          >
                            <FormLabel size={sizes.SMALL}>
                              {translate('Conversion')}
                            </FormLabel>

                            <div className={styles.conversionSettingsLink}>
                              <Link to="/settings/conversion">
                                {translate('AdjustAudiobookConversionSettings')}
                              </Link>
                            </div>
                          </FormGroup>
                      }

                      {
                        isAudiobookProfile && formatItemsValue.length > 0 &&
                          <FormGroup size={sizes.EXTRA_SMALL}>
                            <FormLabel size={sizes.SMALL}>
                              {translate('EasyPresets')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.SELECT}
                              name="easyCustomFormatPreset"
                              value=""
                              values={easyCustomFormatPresetOptions}
                              helpText={translate('EasyPresetsHelpText')}
                              onChange={onEasyCustomFormatPresetChange}
                            />
                          </FormGroup>
                      }

                      {
                        upgradeAllowedField.value &&
                          <FormGroup size={sizes.EXTRA_SMALL}>
                            <FormLabel size={sizes.SMALL}>
                              {translate('UpgradeUntil')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.SELECT}
                              name="cutoff"
                              {...cutoffField}
                              values={qualities}
                              helpText={translate('CutoffHelpText')}
                              onChange={onCutoffChange}
                            />
                          </FormGroup>
                      }

                      {
                        formatItemsValue.length > 0 &&
                          <FormGroup
                            size={sizes.EXTRA_SMALL}
                          >
                            <FormLabel size={sizes.SMALL}>
                              {translate('MinimumCustomFormatScore')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.NUMBER}
                              name="minFormatScore"
                              {...minFormatScoreField}
                              helpText={translate('MinFormatScoreHelpText')}
                              onChange={onInputChange}
                            />
                          </FormGroup>
                      }

                      {
                        upgradeAllowedField.value && formatItemsValue.length > 0 &&
                          <FormGroup
                            size={sizes.EXTRA_SMALL}
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                          >
                            <FormLabel size={sizes.SMALL}>
                              {translate('UpgradeUntilCustomFormatScore')}
                            </FormLabel>

                            <FormInputGroup
                              type={inputTypes.NUMBER}
                              name="cutoffFormatScore"
                              {...cutoffFormatScoreField}
                              helpText={translate('CutoffFormatScoreHelpText')}
                              onChange={onInputChange}
                            />
                          </FormGroup>
                      }

                    </div>

                    <div className={styles.formGroupWrapper}>
                      <QualityProfileItems
                        editGroups={editGroups}
                        qualityProfileItems={itemsValue}
                        errors={itemsField.errors}
                        warnings={itemsField.warnings}
                        {...otherProps}
                      />
                    </div>

                  </div>

                  <div className={styles.customFormatsSection}>
                    {getCustomFormatRender(formatItemsField, advancedSettings, otherProps)}
                  </div>
                </Form>
            }
          </Measure>
        </ModalBody>

        <Measure
          onMeasure={this.onFooterMeasure}
        >
          <ModalFooter>
            {deleteButton}

            <AdvancedSettingsButton
              advancedSettings={advancedSettings}
              onAdvancedSettingsPress={onAdvancedSettingsPress}
              showLabel={false}
            />

            <Button
              onPress={onModalClose}
            >
              {translate('Cancel')}
            </Button>

            <SpinnerErrorButton
              isSpinning={isSaving}
              error={saveError}
              onPress={onSavePress}
            >
              {translate('Save')}
            </SpinnerErrorButton>
          </ModalFooter>
        </Measure>
      </ModalContent>
    );
  }
}

EditQualityProfileModalContent.propTypes = {
  editGroups: PropTypes.bool.isRequired,
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  qualities: PropTypes.arrayOf(PropTypes.object).isRequired,
  convertToQualities: PropTypes.arrayOf(PropTypes.object).isRequired,
  item: PropTypes.object.isRequired,
  isInUse: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onCutoffChange: PropTypes.func.isRequired,
  onConvertToQualityChange: PropTypes.func.isRequired,
  onReleasePriorityChange: PropTypes.func.isRequired,
  onEasyCustomFormatPresetChange: PropTypes.func.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onContentHeightChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteQualityProfilePress: PropTypes.func
};

export default EditQualityProfileModalContent;
