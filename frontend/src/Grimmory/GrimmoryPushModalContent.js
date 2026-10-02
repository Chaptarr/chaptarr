import PropTypes from 'prop-types';
import React, { Component } from 'react';
import CheckInput from 'Components/Form/CheckInput';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './GrimmoryPushModalContent.css';

const grimmoryFields = [
  { name: 'cover', label: 'Cover' },
  { name: 'title', label: 'Title' },
  { name: 'authors', label: 'Author' },
  { name: 'series', label: 'Series' },
  { name: 'description', label: 'Description' },
  { name: 'publisher', label: 'Publisher' },
  { name: 'publisheddate', label: 'PublishedDate' },
  { name: 'language', label: 'Language' },
  { name: 'tags', label: 'Tags' },
  { name: 'identifiers', label: 'Identifiers' }
];

class GrimmoryPushModalContent extends Component {

  constructor(props, context) {
    super(props, context);

    const selected = {};
    grimmoryFields.forEach((field) => {
      selected[field.name] = true;
    });

    this.state = { selected };
  }

  //
  // Listeners

  onFieldChange = ({ name, value }) => {
    this.setState((state) => {
      return { selected: { ...state.selected, [name]: value } };
    });
  };

  onPushPress = () => {
    const fields = grimmoryFields
      .map((field) => field.name)
      .filter((name) => this.state.selected[name]);

    this.props.onPushPress(fields);
  };

  //
  // Render

  render() {
    const {
      bookCount,
      previewValues,
      onModalClose
    } = this.props;

    const {
      selected
    } = this.state;

    const anySelected = grimmoryFields.some((field) => selected[field.name]);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('PushChaptarrMetadataToGrimmory')}
        </ModalHeader>

        <ModalBody>
          <div className={styles.description}>
            {translate('GrimmoryPushDescriptionInterp', [bookCount])}
          </div>

          {
            grimmoryFields.map((field) => {
              const preview = previewValues ? previewValues[field.name] : null;

              return (
                <div key={field.name} className={styles.field}>
                  <div className={styles.check}>
                    <CheckInput
                      name={field.name}
                      value={selected[field.name]}
                      onChange={this.onFieldChange}
                    />
                  </div>
                  <div className={styles.label}>{translate(field.label)}</div>
                  <div className={styles.value}>{preview}</div>
                </div>
              );
            })
          }
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>

          <Button
            kind={kinds.SUCCESS}
            isDisabled={!anySelected}
            onPress={this.onPushPress}
          >
            {translate('PushToGrimmory')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

GrimmoryPushModalContent.propTypes = {
  bookCount: PropTypes.number.isRequired,
  previewValues: PropTypes.object,
  onPushPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default GrimmoryPushModalContent;
