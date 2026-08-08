import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
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
import { icons, inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import ImportCustomFormatModal from './ImportCustomFormatModal';
import AddSpecificationModal from './Specifications/AddSpecificationModal';
import EditSpecificationModalConnector from './Specifications/EditSpecificationModalConnector';
import Specification from './Specifications/Specification';
import styles from './EditCustomFormatModalContent.css';

const appliesToOptions = [
  {
    key: 'both',
    get value() {
      return translate('AudiobooksAndEbooks');
    }
  },
  {
    key: 'audiobook',
    get value() {
      return translate('AudiobooksOnly');
    }
  },
  {
    key: 'ebook',
    get value() {
      return translate('EbooksOnly');
    }
  }
];

function normalizeAppliesTo(value) {
  if (value === 1) {
    return 'audiobook';
  }

  if (value === 2) {
    return 'ebook';
  }

  return value || 'both';
}

class EditCustomFormatModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddSpecificationModalOpen: false,
      isEditSpecificationModalOpen: false,
      isImportCustomFormatModalOpen: false
    };
  }

  //
  // Listeners

  onAddSpecificationPress = () => {
    this.setState({ isAddSpecificationModalOpen: true });
  };

  onAddSpecificationModalClose = ({ specificationSelected = false } = {}) => {
    this.setState({
      isAddSpecificationModalOpen: false,
      isEditSpecificationModalOpen: specificationSelected
    });
  };

  onEditSpecificationModalClose = () => {
    this.setState({ isEditSpecificationModalOpen: false });
  };

  onImportPress = () => {
    this.setState({ isImportCustomFormatModalOpen: true });
  };

  onImportCustomFormatModalClose = () => {
    this.setState({ isImportCustomFormatModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      advancedSettings,
      isFetching,
      error,
      isSaving,
      saveError,
      item,
      specificationsPopulated,
      specifications,
      onInputChange,
      onSavePress,
      onModalClose,
      onDeleteCustomFormatPress,
      onCloneSpecificationPress,
      onConfirmDeleteSpecification,
      ...otherProps
    } = this.props;

    const {
      isAddSpecificationModalOpen,
      isEditSpecificationModalOpen,
      isImportCustomFormatModalOpen
    } = this.state;

    const {
      id,
      name,
      builtInKey,
      appliesTo,
      includeCustomFormatWhenRenaming
    } = item;
    const nameHelpText = builtInKey?.value === 'preferred-narrator' ?
      translate('NarratorMatchCustomFormatHelpText') :
      undefined;
    const appliesToValue = normalizeAppliesTo(appliesTo?.value);

    return (
      <ModalContent onModalClose={onModalClose}>

        <ModalHeader>
          {id ? 'Edit Custom Format' : 'Add Custom Format'}
        </ModalHeader>

        <ModalBody>
          <div>
            {
              isFetching &&
                <LoadingIndicator />
            }

            {
              !isFetching && !!error &&
                <div>
                  {'Unable to add a new custom format, please try again.'}
                </div>
            }

            {
              !isFetching && !error && specificationsPopulated &&
                <div>
                  <Form
                    {...otherProps}
                  >
                    <FormGroup>
                      <FormLabel>
                        {translate('Name')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.TEXT}
                        name="name"
                        helpText={nameHelpText}
                        {...name}
                        onChange={onInputChange}
                      />
                    </FormGroup>

                    <FormGroup>
                      <FormLabel>
                        {translate('AppliesTo')}
                      </FormLabel>

                      <FormInputGroup
                        type={inputTypes.SELECT}
                        name="appliesTo"
                        {...appliesTo}
                        value={appliesToValue}
                        values={appliesToOptions}
                        isDisabled={!!builtInKey?.value}
                        helpText={builtInKey?.value ?
                          translate('BuiltInCustomFormatAppliesToHelpText') :
                          translate('CustomFormatAppliesToHelpText')}
                        onChange={onInputChange}
                      />
                    </FormGroup>
                    <FieldSet legend={translate('Conditions')}>
                      <div className={styles.customFormats}>
                        {
                          specifications.map((tag) => {
                            return (
                              <Specification
                                key={tag.id}
                                {...tag}
                                onCloneSpecificationPress={onCloneSpecificationPress}
                                onConfirmDeleteSpecification={onConfirmDeleteSpecification}
                              />
                            );
                          })
                        }
                      </div>

                      <div className={styles.addCondition}>
                        <Button onPress={this.onAddSpecificationPress}>
                          <span className={styles.addConditionContent}>
                            <Icon name={icons.ADD} />
                            <span>{translate('AddCondition')}</span>
                          </span>
                        </Button>
                      </div>
                    </FieldSet>

                    <FormGroup
                      advancedSettings={advancedSettings}
                      isAdvanced={true}
                    >
                      <FormLabel>{translate('AddCustomFormatNameToFileNames')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.CHECK}
                        name="includeCustomFormatWhenRenaming"
                        helpText={translate('IncludeCustomFormatWhenRenamingHelpText')}
                        {...includeCustomFormatWhenRenaming}
                        onChange={onInputChange}
                      />
                    </FormGroup>
                  </Form>

                  <AddSpecificationModal
                    isOpen={isAddSpecificationModalOpen}
                    onModalClose={this.onAddSpecificationModalClose}
                  />

                  <EditSpecificationModalConnector
                    isOpen={isEditSpecificationModalOpen}
                    onModalClose={this.onEditSpecificationModalClose}
                  />

                  {
                    !id &&
                      <ImportCustomFormatModal
                        isOpen={isImportCustomFormatModalOpen}
                        onModalClose={this.onImportCustomFormatModalClose}
                      />
                  }

                </div>
            }
          </div>
        </ModalBody>
        <ModalFooter>
          {
            id &&
              <Button
                className={styles.deleteButton}
                kind={kinds.DANGER}
                onPress={onDeleteCustomFormatPress}
              >
                {translate('Delete')}
              </Button>
          }

          <div className={styles.footerActions}>
            {
              !id &&
                <Button onPress={this.onImportPress}>
                  {translate('Import')}
                </Button>
            }

            <Button onPress={onModalClose}>
              {translate('Cancel')}
            </Button>

            <SpinnerErrorButton
              isSpinning={isSaving}
              error={saveError}
              onPress={onSavePress}
            >
              {translate('Save')}
            </SpinnerErrorButton>
          </div>
        </ModalFooter>
      </ModalContent>
    );
  }
}

EditCustomFormatModalContent.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  specificationsPopulated: PropTypes.bool.isRequired,
  specifications: PropTypes.arrayOf(PropTypes.object),
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onContentHeightChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteCustomFormatPress: PropTypes.func,
  onCloneSpecificationPress: PropTypes.func.isRequired,
  onConfirmDeleteSpecification: PropTypes.func.isRequired
};

export default EditCustomFormatModalContent;
