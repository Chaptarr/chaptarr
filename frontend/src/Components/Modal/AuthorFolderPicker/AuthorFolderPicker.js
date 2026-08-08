import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AuthorFolderPickerRow from './AuthorFolderPickerRow';
import styles from './AuthorFolderPicker.css';

class AuthorFolderPicker extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      selectedPath: null
    };
  }

  //
  // Listeners

  onSelectFolder = (path) => {
    this.setState({ selectedPath: path });
  }

  onLinkClick = () => {
    const { selectedPath } = this.state;
    const { onLinkAuthorPress } = this.props;

    if (selectedPath) {
      onLinkAuthorPress(selectedPath);
    }
  }

  //
  // Render

  render() {
    const {
      isLinking,
      authorName,
      matches,
      onModalClose
    } = this.props;

    const { selectedPath } = this.state;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('SelectAuthorFolder')}
        </ModalHeader>

        <ModalBody className={styles.modalBody}>
          <div className={styles.info}>
            {translate('MultipleMatchingFoldersFound', { authorName })}
          </div>

          <div className={styles.folders}>
            {
              matches.map((match) => {
                return (
                  <AuthorFolderPickerRow
                    key={match.path}
                    path={match.path}
                    folderName={match.folderName}
                    confidenceScore={match.confidenceScore}
                    matchReason={match.matchReason}
                    isSelected={selectedPath === match.path}
                    onPress={this.onSelectFolder}
                  />
                );
              })
            }
          </div>

          <div className={styles.skipInfo}>
            {translate('SkipToLeaveUnlinked')}
          </div>
        </ModalBody>

        <ModalFooter>
          <Button
            onPress={onModalClose}
          >
            {translate('Skip')}
          </Button>

          <Button
            kind={kinds.PRIMARY}
            isDisabled={!selectedPath || isLinking}
            onPress={this.onLinkClick}
          >
            {isLinking ? <LoadingIndicator /> : translate('LinkAuthor')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

AuthorFolderPicker.propTypes = {
  isLinking: PropTypes.bool.isRequired,
  authorName: PropTypes.string.isRequired,
  matches: PropTypes.arrayOf(PropTypes.shape({
    path: PropTypes.string.isRequired,
    folderName: PropTypes.string.isRequired,
    confidenceScore: PropTypes.number.isRequired,
    matchReason: PropTypes.string
  })).isRequired,
  onLinkAuthorPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default AuthorFolderPicker;