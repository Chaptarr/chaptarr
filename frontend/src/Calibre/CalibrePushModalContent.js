import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './CalibrePushModalContent.css';

const calibreFields = [
  { name: 'cover', label: 'Cover' },
  { name: 'title', label: 'Title', movesFiles: true },
  { name: 'authors', label: 'Author', movesFiles: true },
  { name: 'series', label: 'Series' },
  { name: 'comments', label: 'Description' },
  { name: 'publisher', label: 'Publisher' },
  { name: 'pubdate', label: 'Publish Date' },
  { name: 'languages', label: 'Language' },
  { name: 'tags', label: 'Tags' },
  { name: 'rating', label: 'Rating' },
  { name: 'identifiers', label: 'Identifiers' }
];

class CalibrePushModalContent extends Component {

  constructor(props, context) {
    super(props, context);

    const selected = {};
    calibreFields.forEach((field) => {
      selected[field.name] = !field.movesFiles;
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
    const fields = calibreFields
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

    const { selected } = this.state;
    const anySelected = calibreFields.some((field) => selected[field.name]);
    const willMoveFiles = calibreFields.some((field) => field.movesFiles && selected[field.name]);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('PushMetadataToCalibre')}
        </ModalHeader>

        <ModalBody>
          {
            (!previewValues || willMoveFiles) ?
              <div className={styles.notices}>
                {
                  previewValues ?
                    null :
                    <Alert>
                      {translate('CalibrePushTipPreview')}
                      <Icon
                        className={styles.tipIcon}
                        name={icons.TAGS}
                      />
                    </Alert>
                }

                {
                  willMoveFiles ?
                    <Alert kind={kinds.WARNING}>
                      {translate('CalibrePushMovesFilesWarning')}
                    </Alert> :
                    null
                }
              </div> :
              null
          }

          <div className={styles.description}>
            {translate('CalibrePushDescriptionInterp', [bookCount])}
          </div>

          {
            calibreFields.map((field) => {
              const preview = previewValues ? previewValues[field.name] : null;

              return (
                <div
                  key={field.name}
                  className={styles.field}
                >
                  <div className={styles.check}>
                    <CheckInput
                      name={field.name}
                      value={selected[field.name]}
                      onChange={this.onFieldChange}
                    />
                  </div>

                  <div className={styles.label}>
                    {field.label}
                  </div>

                  <div className={styles.value}>
                    {preview}
                  </div>
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
            {translate('PushToCalibre')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

CalibrePushModalContent.propTypes = {
  bookCount: PropTypes.number.isRequired,
  previewValues: PropTypes.object,
  onPushPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default CalibrePushModalContent;
