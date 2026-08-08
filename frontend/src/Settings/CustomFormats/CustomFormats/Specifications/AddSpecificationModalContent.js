import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddSpecificationItem from './AddSpecificationItem';
import styles from './AddSpecificationModalContent.css';

const AUDIOBOOK_IMPLEMENTATIONS = [
  'NarratorNamesSpecification',
  'PreferredNarratorSpecification',
  'AudioProductionSpecification'
];

const DESCRIPTION_KEYS = {
  AudioProductionSpecification: 'CustomFormatsAudioProductionConditionDescription',
  IndexerFlagSpecification: 'CustomFormatsIndexerFlagConditionDescription',
  NarratorNamesSpecification: 'CustomFormatsNarratorNamesConditionDescription',
  NarratorSpecification: 'CustomFormatsNarratorRegexConditionDescription',
  PreferredNarratorSpecification: 'CustomFormatsSelectedNarratorsConditionDescription',
  ReleaseGroupSpecification: 'CustomFormatsReleaseGroupConditionDescription',
  ReleaseTitleSpecification: 'CustomFormatsReleaseTitleConditionDescription',
  SizeSpecification: 'CustomFormatsSizeConditionDescription'
};

function getDisplayName(specification) {
  if (specification.implementation === 'AudioProductionSpecification') {
    return translate('CustomFormatsAudioProductionConditionName');
  }

  return specification.implementationName;
}

function getDescription(specification) {
  const key = DESCRIPTION_KEYS[specification.implementation];
  return key ? translate(key) : '';
}

class AddSpecificationModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      showAdvancedReleaseConditions: false
    };
  }

  //
  // Listeners

  onToggleAdvancedPress = () => {
    this.setState({
      showAdvancedReleaseConditions: !this.state.showAdvancedReleaseConditions
    });
  };

  //
  // Render

  renderSpecification = (specification) => {
    return (
      <AddSpecificationItem
        key={specification.implementation}
        {...specification}
        implementationName={getDisplayName(specification)}
        description={getDescription(specification)}
        onSpecificationSelect={this.props.onSpecificationSelect}
      />
    );
  };

  render() {
    const {
      isSchemaFetching,
      isSchemaPopulated,
      schemaError,
      schema,
      onModalClose
    } = this.props;

    const audiobookConditions = schema
      .filter((specification) => AUDIOBOOK_IMPLEMENTATIONS.includes(specification.implementation))
      .sort((left, right) => {
        return AUDIOBOOK_IMPLEMENTATIONS.indexOf(left.implementation) -
          AUDIOBOOK_IMPLEMENTATIONS.indexOf(right.implementation);
      });
    const advancedConditions = schema
      .filter((specification) => !AUDIOBOOK_IMPLEMENTATIONS.includes(specification.implementation));
    const { showAdvancedReleaseConditions } = this.state;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('AddCondition')}
        </ModalHeader>

        <ModalBody>
          {
            isSchemaFetching &&
              <LoadingIndicator />
          }

          {
            !isSchemaFetching && !!schemaError &&
              <div>
                {'Unable to add a new condition, please try again.'}
              </div>
          }

          {
            isSchemaPopulated && !schemaError &&
              <div>
                <Alert kind={kinds.INFO}>
                  {translate('CustomFormatsConditionIntro')}
                </Alert>

                <div className={styles.sectionTitle}>
                  {translate('CustomFormatsAudiobookConditions')}
                </div>

                <div className={styles.specifications}>
                  {audiobookConditions.map(this.renderSpecification)}
                </div>

                {
                  advancedConditions.length > 0 &&
                    <div className={styles.advancedToggle}>
                      <Button onPress={this.onToggleAdvancedPress}>
                        {
                          translate(showAdvancedReleaseConditions ?
                            'CustomFormatsHideAdvancedReleaseConditions' :
                            'CustomFormatsShowAdvancedReleaseConditions')
                        }
                      </Button>
                    </div>
                }

                {
                  showAdvancedReleaseConditions &&
                    <div>
                      <div className={styles.sectionTitle}>
                        {translate('CustomFormatsAdvancedReleaseConditions')}
                      </div>

                      <div className={styles.specifications}>
                        {advancedConditions.map(this.renderSpecification)}
                      </div>
                    </div>
                }
              </div>
          }
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

AddSpecificationModalContent.propTypes = {
  isSchemaFetching: PropTypes.bool.isRequired,
  isSchemaPopulated: PropTypes.bool.isRequired,
  schemaError: PropTypes.object,
  schema: PropTypes.arrayOf(PropTypes.object).isRequired,
  onSpecificationSelect: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default AddSpecificationModalContent;
