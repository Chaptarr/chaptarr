import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import RootFolderSelectInputConnector from 'Components/Form/RootFolderSelectInputConnector';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import { FolderType } from 'Helpers/Props/folderTypes';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

class SelectAuthorRootFolderModalContent extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      rootFolderPath: props.rootFolderPath || ''
    };
  }

  componentDidUpdate(prevProps) {
    if (prevProps.mediaType !== this.props.mediaType) {
      this.setState({ rootFolderPath: this.props.rootFolderPath || '' });
    }
  }

  onRootFolderChange = ({ value }) => {
    this.setState({ rootFolderPath: value });
  };

  onSavePress = () => {
    this.props.onSavePress(this.state.rootFolderPath);
  };

  render() {
    const {
      mediaType,
      isSaving,
      saveError,
      onModalClose
    } = this.props;

    const folderType = mediaType === 'ebook' ? FolderType.Ebook : FolderType.Audiobook;
    const label = mediaType === 'ebook' ? translate('EbookRootFolder') : translate('AudiobookRootFolder');

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('SelectRootFolder')}
        </ModalHeader>

        <ModalBody>
          {
            saveError &&
              <Alert kind={kinds.DANGER}>
                {getErrorMessage(saveError, 'Unable to save author')}
              </Alert>
          }

          <RootFolderSelectInputConnector
            name="rootFolderPath"
            value={this.state.rootFolderPath}
            includeNoChange={false}
            includeMissingValue={true}
            includeMixed={false}
            folderType={folderType}
            onChange={this.onRootFolderChange}
          />

          <div style={{ marginTop: 8, color: '#888' }}>
            {translate('SelectRootFolderForAuthor', { label })}
          </div>
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>

          <SpinnerButton
            isSpinning={isSaving}
            isDisabled={!this.state.rootFolderPath}
            onPress={this.onSavePress}
          >
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    );
  }
}

SelectAuthorRootFolderModalContent.propTypes = {
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  rootFolderPath: PropTypes.string,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired
};

export default SelectAuthorRootFolderModalContent;
