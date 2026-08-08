import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './DeleteAuthorModalContent.css';

class DeleteAuthorModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      deleteFiles: false,
      addImportListExclusion: false,
      readdAuthor: false
    };
  }

  //
  // Listeners

  onDeleteFilesChange = ({ value }) => {
    this.setState({
      deleteFiles: value,
      readdAuthor: value ? false : this.state.readdAuthor
    });
  };

  onAddImportListExclusionChange = ({ value }) => {
    this.setState({ addImportListExclusion: value });
  };

  onReaddAuthorChange = ({ value }) => {
    this.setState({ readdAuthor: value });
  };

  onDeleteAuthorConfirmed = () => {
    const deleteFiles = this.state.deleteFiles;
    const addImportListExclusion = this.state.addImportListExclusion;
    const readdAuthor = this.state.readdAuthor;

    this.setState({ deleteFiles: false, addImportListExclusion: false, readdAuthor: false });
    this.props.onDeletePress(deleteFiles, addImportListExclusion, readdAuthor);
  };

  //
  // Render

  render() {
    const {
      authorName,
      path,
      statistics,
      onModalClose
    } = this.props;

    const {
      bookFileCount,
      sizeOnDisk
    } = statistics;

    const deleteFiles = this.state.deleteFiles;
    const addImportListExclusion = this.state.addImportListExclusion;
    const readdAuthor = this.state.readdAuthor;

    let deleteFilesLabel = `Delete ${bookFileCount} Book Files`;
    let deleteFilesHelpText = translate('DeleteFilesHelpText');

    if (bookFileCount === 0) {
      deleteFilesLabel = 'Delete Author Folder';
      deleteFilesHelpText = 'Delete the author folder and its contents';
    }

    return (
      <ModalContent
        onModalClose={onModalClose}
      >
        <ModalHeader>
          {translate('DeleteAuthorHeader', { authorName })}
        </ModalHeader>

        <ModalBody>
          <div className={styles.pathContainer}>
            <Icon
              className={styles.pathIcon}
              name={icons.FOLDER}
            />

            {path}
          </div>

          <FormGroup>
            <FormLabel>{deleteFilesLabel}</FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="deleteFiles"
              value={deleteFiles}
              helpText={deleteFilesHelpText}
              kind={kinds.DANGER}
              onChange={this.onDeleteFilesChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('AddListExclusion')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="addImportListExclusion"
              value={addImportListExclusion}
              helpText={translate('AddImportListExclusionHelpText')}
              kind={kinds.DANGER}
              onChange={this.onAddImportListExclusionChange}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('PurgeAndReaddAuthor')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="readdAuthor"
              value={readdAuthor}
              isDisabled={deleteFiles}
              helpText={translate('PurgeAndReaddAuthorHelpText')}
              kind={kinds.DANGER}
              onChange={this.onReaddAuthorChange}
            />
          </FormGroup>

          {
            deleteFiles &&
              <div className={styles.deleteFilesMessage}>
                <div>
                  {translate('TheAuthorFolderAndAllOfItsContentWillBeDeleted', [path])}
                </div>

                {
                  !!bookFileCount &&
                    <div>{translate('BookFilesTotalingSize', { count: bookFileCount, size: formatBytes(sizeOnDisk) })}</div>
                }
              </div>
          }

        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Close')}
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
  authorName: PropTypes.string.isRequired,
  path: PropTypes.string.isRequired,
  statistics: PropTypes.object.isRequired,
  onDeletePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

DeleteAuthorModalContent.defaultProps = {
  statistics: {
    bookFileCount: 0
  }
};

export default DeleteAuthorModalContent;
