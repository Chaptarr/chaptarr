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
import formatBytes from 'Utilities/Number/formatBytes';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import styles from './DeleteBookModalContent.css';

function formatMediaTypeLabel(mediaType) {
  return mediaType === 'ebook' ? 'eBook' : 'audiobook';
}

function formatMediaTypeCount(count, mediaType) {
  if (!count) {
    return null;
  }

  let label = null;

  if (mediaType === 'ebook') {
    label = count === 1 ? 'eBook' : 'eBooks';
  } else {
    label = count === 1 ? 'audiobook' : 'audiobooks';
  }

  return `${count} ${label}`;
}

function getSiblingDeleteLabel(currentMediaType, siblingBook) {
  const siblings = siblingBook?.siblings ?? [];

  if (siblings.length === 1) {
    const siblingMediaType = siblings[0].mediaType;
    const label = formatMediaTypeLabel(siblingMediaType);

    if (siblingMediaType === currentMediaType) {
      return `Delete the matched ${label} clone as well`;
    }

    return `Delete the matched ${label} version as well`;
  }

  const counts = [
    formatMediaTypeCount(siblingBook?.audiobookCount, 'audiobook'),
    formatMediaTypeCount(siblingBook?.ebookCount, 'ebook')
  ].filter(Boolean);

  return counts.length ?
    `Delete all matched copies of this book (${counts.join(', ')})` :
    'Delete all matched copies of this book';
}

function getFileCount(details) {
  return details.reduce((total, detail) => total + ((detail.files ?? []).length), 0);
}

function getFileSize(details) {
  return details.reduce((total, detail) => {
    return total + (detail.files ?? []).reduce((fileTotal, file) => fileTotal + (file.size ?? 0), 0);
  }, 0);
}

class DeleteBookModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      deleteFiles: false,
      addImportListExclusion: true,
      applyToBothFormats: false,
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

  onApplyToBothFormatsChange = ({ value }) => {
    this.setState({ applyToBothFormats: value });
  };

  getDeletionBookIds = () => [this.props.bookId, ...(this.state.applyToBothFormats ?
    (this.props.siblingBook?.siblings ?? []).map((book) => book.bookId) : [])];

  onPreviewReady = (readySelection) => this.setState({ readySelection });

  onDeleteBookConfirmed = () => {
    if (this.state.deleteFiles && this.state.readySelection !== deletionSelection([], this.getDeletionBookIds())) {
      return;
    }

    const deleteFiles = this.state.deleteFiles;
    const addImportListExclusion = this.state.addImportListExclusion;
    const applyToBothFormats = this.state.applyToBothFormats;

    this.setState({ hasSubmitted: true });
    this.props.onDeletePress(deleteFiles, addImportListExclusion, applyToBothFormats);
  };

  //
  // Render

  render() {
    const {
      title,
      statistics,
      mediaType,
      siblingBook,
      currentBookDeleteInfo,
      isDeleting,
      deleteError,
      onModalClose
    } = this.props;

    const {
      bookFileCount = 0,
      sizeOnDisk = 0
    } = statistics;

    const deleteFiles = this.state.deleteFiles;
    const addImportListExclusion = this.state.addImportListExclusion;
    const applyToBothFormats = this.state.applyToBothFormats;
    const currentFormatLabel = formatMediaTypeLabel(mediaType);
    const currentBookDetail = currentBookDeleteInfo ?? siblingBook?.currentBook ?? {
      bookId: this.props.bookId,
      title,
      mediaType,
      files: []
    };
    const siblingDetails = siblingBook?.siblings ?? [];
    const siblingStatistics = siblingBook?.statistics ?? {};
    const currentFileCount = currentBookDetail.files?.length || bookFileCount;
    const currentSizeOnDisk = currentBookDetail.files?.length ? getFileSize([currentBookDetail]) : sizeOnDisk;
    const siblingFileCount = getFileCount(siblingDetails) || siblingStatistics.bookFileCount || 0;
    const siblingSizeOnDisk = getFileSize(siblingDetails) || siblingStatistics.sizeOnDisk || 0;
    const totalBookFileCount = currentFileCount + (applyToBothFormats ? siblingFileCount : 0);
    const totalSizeOnDisk = currentSizeOnDisk + (applyToBothFormats ? siblingSizeOnDisk : 0);

    const deleteFilesLabel = applyToBothFormats && siblingBook ?
      'Delete files for all matched books' :
      `Delete files for this ${currentFormatLabel}`;
    const deleteFilesHelpText = applyToBothFormats && siblingBook ?
      `If enabled, Chaptarr will delete files for this ${currentFormatLabel} and all matched copies of the same book.` :
      `Delete files for this ${currentFormatLabel}.`;
    const addImportListExclusionLabel = applyToBothFormats && siblingBook ?
      'Don\'t add these books back' :
      `Don't add this ${currentFormatLabel} back`;
    const addImportListExclusionHelpText = applyToBothFormats && siblingBook ?
      'Chaptarr will skip re-adding both formats during refresh and import-list sync until you manually add them again.' :
      `Chaptarr will skip re-adding this ${currentFormatLabel} during refresh and import-list sync until you manually add it again.`;
    const siblingDeleteLabel = siblingBook ?
      getSiblingDeleteLabel(mediaType, siblingBook) :
      null;
    const siblingDeleteHelpText = siblingBook ?
      `If enabled, file deletion and "don't add back" apply to this ${currentFormatLabel} and all matched copies of the same book.` :
      null;

    return (
      <ModalContent
        onModalClose={onModalClose}
      >
        <ModalHeader>
          {translate('DeleteBookHeader', { title })}
        </ModalHeader>

        <ModalBody registerScroller={this.registerScroller}>
          {this.state.hasSubmitted && deleteError &&
            <Alert kind={kinds.DANGER} role="alert">
              {getErrorMessage(deleteError, translate('BookDeleteFailed'))}
            </Alert>
          }

          <FormGroup>
            <FormLabel>{deleteFilesLabel}</FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="deleteFiles"
              value={deleteFiles}
              isDisabled={isDeleting}
              helpText={deleteFilesHelpText}
              kind={kinds.DANGER}
              onChange={this.onDeleteFilesChange}
            />
          </FormGroup>

          {
            !!siblingBook &&
              <FormGroup>
                <FormLabel>{siblingDeleteLabel}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="applyToBothFormats"
                  value={applyToBothFormats}
                  isDisabled={isDeleting}
                  helpText={siblingDeleteHelpText}
                  kind={kinds.DANGER}
                  onChange={this.onApplyToBothFormatsChange}
                />
              </FormGroup>
          }

          <FormGroup>
            <FormLabel>
              {addImportListExclusionLabel}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="addImportListExclusion"
              value={addImportListExclusion}
              isDisabled={isDeleting}
              helpText={addImportListExclusionHelpText}
              kind={kinds.DANGER}
              onChange={this.onAddImportListExclusionChange}
            />
          </FormGroup>

          {
            !addImportListExclusion &&
              <div className={styles.deleteFilesMessage}>
                <div>
                  {
                    applyToBothFormats && siblingBook ?
                      'If you do not block re-adds, these books may come back during the next author refresh or import-list sync.' :
                      'If you do not block re-adds, this book may come back during the next author refresh or import-list sync.'
                  }
                </div>
              </div>
          }

          {
            deleteFiles &&
              <div className={styles.deleteFilesMessage}>
                <div>
                  {applyToBothFormats && siblingBook ? 'Files for all matched books will be deleted.' : 'The book files will be deleted.'}
                </div>

                {
                  !!totalBookFileCount &&
                    <div>{translate('BookFilesTotalingSize', { count: totalBookFileCount, size: formatBytes(totalSizeOnDisk) })}</div>
                }

                <BookFileDeletionPreview key={deletionSelection([], this.getDeletionBookIds())}
                  bookIds={this.getDeletionBookIds()} onReadyChange={this.onPreviewReady}
                />
              </div>
          }

        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose} isDisabled={isDeleting}>
            {translate('Close')}
          </Button>

          <SpinnerButton
            kind={kinds.DANGER}
            aria-label={translate('Delete')}
            isSpinning={isDeleting}
            isDisabled={deleteFiles && this.state.readySelection !== deletionSelection([], this.getDeletionBookIds())}
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
  bookId: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  mediaType: PropTypes.string,
  siblingBook: PropTypes.object,
  currentBookDeleteInfo: PropTypes.object,
  statistics: PropTypes.object.isRequired,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  onDeletePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

DeleteBookModalContent.defaultProps = {
  mediaType: 'audiobook',
  isDeleting: false,
  siblingBook: null,
  currentBookDeleteInfo: null,
  statistics: {
    bookFileCount: 0,
    sizeOnDisk: 0
  }
};

export default DeleteBookModalContent;
