import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookFileDeletionPreview, { deletionSelection } from 'BookFile/Editor/BookFileDeletionPreview';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import styles from './DeleteBookModalContent.css';

class DeleteBookModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      deleteFiles: false,
      addImportListExclusion: true,
      hasSubmitted: false,
      readySelection: null
    };
  }

  componentDidUpdate(prevProps) {
    if (this.props.deleteError && !prevProps.deleteError && this.scroller) {
      this.scroller.scrollTop = 0;
    }
  }

  registerScroller = (scroller) => {
    this.scroller = scroller;
  };

  //
  // Listeners

  onDeleteFilesChange = ({ value }) => {
    this.setState({ deleteFiles: value });
  };

  onAddImportListExclusionChange = ({ value }) => {
    this.setState({ addImportListExclusion: value });
  };

  onPreviewReady = (readySelection) => this.setState({ readySelection });

  onDeleteBookConfirmed = () => {
    if (this.state.deleteFiles && this.state.readySelection !== deletionSelection([], this.props.bookIds)) {
      return;
    }

    const {
      deleteFiles,
      addImportListExclusion
    } = this.state;

    this.setState({ hasSubmitted: true });
    this.props.onDeleteSelectedPress(deleteFiles, addImportListExclusion);
  };

  //
  // Render

  render() {
    const {
      book,
      bookIds,
      selectedCount,
      isDeleting,
      deleteError,
      onModalClose
    } = this.props;

    const {
      deleteFiles,
      addImportListExclusion
    } = this.state;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('DeleteSelectedBook')}
        </ModalHeader>

        <ModalBody registerScroller={this.registerScroller}>
          {this.state.hasSubmitted && deleteError &&
            <Alert kind={kinds.DANGER} role="alert">
              {getErrorMessage(deleteError, translate('BookDeleteFailed'))}
            </Alert>
          }
          <div>
            <FormGroup>
              <FormLabel>{translate('DeleteFiles')}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="deleteFiles"
                value={deleteFiles}
                helpText={translate('DeleteFilesHelpText')}
                kind={kinds.DANGER}
                isDisabled={isDeleting || selectedCount === 0}
                onChange={this.onDeleteFilesChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{translate('AddListExclusion')}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="addImportListExclusion"
                value={addImportListExclusion}
                isDisabled={isDeleting}
                helpText={translate('AddImportListExclusionHelpText')}
                kind={kinds.DANGER}
                onChange={this.onAddImportListExclusionChange}
              />
            </FormGroup>

            {
              !addImportListExclusion &&
                <div className={styles.deleteFilesMessage}>
                  <div>
                    {translate('IfYouDontAddAnImportListExclusionAndTheAuthorHasAMetadataProfileOtherThanNoneThenThisBookMayBeReaddedDuringTheNextAuthorRefresh')}
                  </div>
                </div>
            }

          </div>

          <div className={styles.message}>
            {translate(deleteFiles ? 'DeleteSelectedBooksAndFilesMessageText' : 'DeleteSelectedBooksMessageText', { count: selectedCount })}
          </div>

          {
            book.length < selectedCount &&
              <div className={styles.deleteFilesMessage}>
                {translate('AdditionalSelectedBooksNotLoaded', { count: selectedCount - book.length })}
              </div>
          }

          <ul>
            {
              book.map((s) => {
                return (
                  <li key={s.title}>
                    <span>{s.title}</span>
                  </li>
                );
              })
            }
          </ul>

          {
            deleteFiles &&
              <div>
                <div className={styles.deleteFilesMessage}>
                  {translate('TheFollowingFilesWillBeDeleted')}
                </div>
                <BookFileDeletionPreview key={deletionSelection([], bookIds)} bookIds={bookIds}
                  onReadyChange={this.onPreviewReady}
                />
              </div>
          }
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose} isDisabled={isDeleting}>
            {translate('Cancel')}
          </Button>

          <SpinnerButton
            kind={kinds.DANGER}
            aria-label={translate('Delete')}
            isSpinning={isDeleting}
            isDisabled={selectedCount === 0 || (deleteFiles && this.state.readySelection !== deletionSelection([], bookIds))}
            onPress={this.onDeleteBookConfirmed}
          >
            {translate('Delete')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    );
  }
}

DeleteBookModalContent.propTypes = {
  book: PropTypes.arrayOf(PropTypes.object).isRequired,
  bookIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  selectedCount: PropTypes.number.isRequired,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  onModalClose: PropTypes.func.isRequired,
  onDeleteSelectedPress: PropTypes.func.isRequired
};

DeleteBookModalContent.defaultProps = {
  isDeleting: false
};

export default DeleteBookModalContent;
