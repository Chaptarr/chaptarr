import PropTypes from 'prop-types';
import React from 'react';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, inputTypes, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './EditMetadataProfileModalContent.css';

function fieldWithDefault(field, value) {
  return field ?? {
    value,
    errors: [],
    warnings: []
  };
}

function EditMetadataProfileModalContent(props) {
  const {
    isFetching,
    error,
    isSaving,
    saveError,
    item,
    isInUse,
    onInputChange,
    onSavePress,
    onModalClose,
    onDeleteMetadataProfilePress,
    profileType: requestedProfileType,
    ...otherProps
  } = props;

  const {
    id,
    name,
    profileType,
    minPopularity,
    skipMissingDate,
    skipMissingIsbn,
    skipMissingAsin,
    skipPartsAndSets,
    skipSeriesSecondary,
    skipMissingIdentifierOmnibus,
    skipOmnibus,
    allowedLanguages,
    ignored,
    minPages
  } = item;
  const nameField = fieldWithDefault(name, '');
  const minPopularityField = fieldWithDefault(minPopularity, 0);
  const minPagesField = fieldWithDefault(minPages, 0);
  const skipMissingDateField = fieldWithDefault(skipMissingDate, false);
  const skipMissingIsbnField = fieldWithDefault(skipMissingIsbn, false);
  const skipMissingAsinField = fieldWithDefault(skipMissingAsin, false);
  const skipPartsAndSetsField = fieldWithDefault(skipPartsAndSets, false);
  const skipSeriesSecondaryField = fieldWithDefault(skipSeriesSecondary, false);
  const skipMissingIdentifierOmnibusField = fieldWithDefault(skipMissingIdentifierOmnibus, false);
  const skipOmnibusField = fieldWithDefault(skipOmnibus, false);
  const allowedLanguagesField = fieldWithDefault(allowedLanguages, '');
  const ignoredField = fieldWithDefault(ignored, []);

  // profileType: 0=General, 1=Audiobook, 2=Ebook
  const profileTypeValue = profileType?.value ?? requestedProfileType;
  const isAudiobook = profileTypeValue === 1;
  const labelSuffix = isAudiobook ? 'Audiobook' : 'Book';
  const deleteDisabledTooltip = translate('IsInUseCantDeleteAMetadataProfileThatIsAttachedToAnAuthorImportListOrRootFolder');

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {id ? translate('EditMetadataProfile') : translate('AddMetadataProfile')}
      </ModalHeader>

      <ModalBody>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <div>
              {translate('UnableToAddANewMetadataProfilePleaseTryAgain')}
            </div>
        }

        {
          !isFetching && !error &&
            <Form {...otherProps}>
              <FormGroup>
                <FormLabel>
                  {translate('Name')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="name"
                  {...nameField}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('MinimumPopularity')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="minPopularity"
                  {...minPopularityField}
                  helpText={translate('MinPopularityHelpText')}
                  isFloat={true}
                  min={0}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('MinimumPages')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="minPages"
                  {...minPagesField}
                  helpText={translate('MinPagesHelpText')}
                  min={0}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipMissingDate${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipMissingDate"
                  {...skipMissingDateField}
                  helpText={translate(`SkipMissingDate${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipMissingIdentifier${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipMissingIsbn"
                  {...skipMissingIsbnField}
                  helpText={translate(`SkipMissingIdentifier${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipMissingAsin${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipMissingAsin"
                  {...skipMissingAsinField}
                  helpText={translate(`SkipMissingAsin${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipPartsAndSets${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipPartsAndSets"
                  {...skipPartsAndSetsField}
                  helpText={translate(`SkipPartsAndSets${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipSeriesSecondary${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipSeriesSecondary"
                  {...skipSeriesSecondaryField}
                  helpText={translate(`SkipSeriesSecondary${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate(`SkipMissingIdentifierOmnibus${labelSuffix}`)}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="skipMissingIdentifierOmnibus"
                  {...skipMissingIdentifierOmnibusField}
                  helpText={translate(`SkipMissingIdentifierOmnibus${labelSuffix}HelpText`)}
                  onChange={onInputChange}
                />
              </FormGroup>

              {
                (skipMissingIdentifierOmnibusField.value || skipOmnibusField.value) &&
                  <FormGroup>
                    <FormLabel>
                      {translate(`SkipOmnibus${labelSuffix}`)}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="skipOmnibus"
                      {...skipOmnibusField}
                      helpText={translate(`SkipOmnibus${labelSuffix}HelpText`)}
                      onChange={onInputChange}
                    />
                  </FormGroup>
              }

              <FormGroup>
                <FormLabel>
                  {translate('AllowedLanguages')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT_TAG}
                  name="allowedLanguages"
                  {...allowedLanguagesField}
                  helpText={translate('AllowedLanguagesHelpText')}
                  placeholder={translate('AllowedLanguagesPlaceholder')}
                  kind={kinds.SUCCESS}
                  delimiters={['Tab', 'Enter', ',']}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('MustNotContain')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT_TAG}
                  name="ignored"
                  helpText={translate('IgnoredMetaHelpText')}
                  kind={kinds.DANGER}
                  placeholder={translate('IgnoredPlaceHolder')}
                  delimiters={['Tab', 'Enter', ',']}
                  {...ignoredField}
                  onChange={onInputChange}
                />
              </FormGroup>

            </Form>
        }
      </ModalBody>
      <ModalFooter>
        {
          id &&
            (
              isInUse ?
                <Tooltip
                  className={styles.deleteButtonContainer}
                  position={tooltipPositions.TOP}
                  anchor={
                    <span className={styles.deleteButtonWithReason}>
                      <Button
                        kind={kinds.DANGER}
                        isDisabled={true}
                        onPress={onDeleteMetadataProfilePress}
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
                /> :
                <div className={styles.deleteButtonContainer}>
                  <Button
                    kind={kinds.DANGER}
                    onPress={onDeleteMetadataProfilePress}
                  >
                    {translate('Delete')}
                  </Button>
                </div>
            )
        }

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
    </ModalContent>
  );
}

EditMetadataProfileModalContent.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  isInUse: PropTypes.bool.isRequired,
  profileType: PropTypes.number,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteMetadataProfilePress: PropTypes.func
};

export default EditMetadataProfileModalContent;
