import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './DeleteAuthorModalContent.css';

class DeleteAuthorModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      deleteFiles: false
    };
  }

  //
  // Listeners

  onDeleteFilesChange = ({ value }) => {
    this.setState({ deleteFiles: value });
  };

  onDeleteAuthorConfirmed = () => {
    const deleteFiles = this.state.deleteFiles;

    this.setState({ deleteFiles: false });
    this.props.onDeleteSelectedPress(deleteFiles);
  };

  //
  // Render

  render() {
    const {
      author,
      onModalClose
    } = this.props;
    const deleteFiles = this.state.deleteFiles;
    const hasMultipleAuthors = author.length > 1;
    let confirmationKey = hasMultipleAuthors ? 'DeleteSelectedAuthorsConfirm' : 'DeleteOneAuthorConfirm';

    if (deleteFiles) {
      confirmationKey = hasMultipleAuthors ? 'DeleteSelectedAuthorsAndContentsConfirm' : 'DeleteOneAuthorAndContentsConfirm';
    }

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('DeleteSelectedAuthor')}
        </ModalHeader>

        <ModalBody>
          <div>
            <FormGroup>
              <FormLabel>
                {translate(hasMultipleAuthors ? 'DeleteAuthorFoldersLabel' : 'DeleteAuthorFolderLabel')}
              </FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="deleteFiles"
                value={deleteFiles}
                helpText={translate(hasMultipleAuthors ? 'DeleteAuthorFoldersContentsHelpText' : 'DeleteAuthorFolderContentsHelpText')}
                kind={kinds.DANGER}
                onChange={this.onDeleteFilesChange}
              />
            </FormGroup>
          </div>

          <div className={styles.message}>
            {translate(confirmationKey, { count: author.length })}
          </div>

          <ul>
            {
              author.map((s) => {
                return (
                  <li key={s.authorName}>
                    <span>{s.authorName}</span>

                    {
                      deleteFiles &&
                        <span className={styles.pathContainer}>
                          -
                          <span className={styles.path}>
                            {s.path}
                          </span>
                        </span>
                    }
                  </li>
                );
              })
            }
          </ul>
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>

          <Button
            kind={kinds.DANGER}
            onPress={this.onDeleteAuthorConfirmed}
          >
            {translate('Delete')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

DeleteAuthorModalContent.propTypes = {
  author: PropTypes.arrayOf(PropTypes.object).isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteSelectedPress: PropTypes.func.isRequired
};

export default DeleteAuthorModalContent;
