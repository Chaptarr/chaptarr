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
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './DeleteBookModalContent.css';

function formatMediaTypeLabel(mediaType) {
  return translate(mediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel');
}

function formatMediaTypeCount(count, mediaType) {
  if (!count) {
    return null;
  }

  let labelKey = count === 1 ? 'AudiobookLabel' : 'Audiobooks';

  if (mediaType === 'ebook') {
    labelKey = count === 1 ? 'EbookLabel' : 'Ebooks';
  }

  return `${count} ${translate(labelKey)}`;
}

function getSiblingDeleteLabel(currentMediaType, siblingBook) {
  const siblings = siblingBook?.siblings ?? [];

  if (siblings.length === 1) {
    const siblingMediaType = siblings[0].mediaType;
    const label = formatMediaTypeLabel(siblingMediaType);

    if (siblingMediaType === currentMediaType) {
      return translate('DeleteMatchedMediaTypeCloneLabel', { mediaType: label });
    }

    return translate('DeleteMatchedMediaTypeVersionLabel', { mediaType: label });
  }

  const counts = [
    formatMediaTypeCount(siblingBook?.audiobookCount, 'audiobook'),
    formatMediaTypeCount(siblingBook?.ebookCount, 'ebook')
  ].filter(Boolean);

  return counts.length ?
    translate('DeleteAllMatchedBookCopiesWithCountsLabel', { counts: counts.join(', ') }) :
    translate('DeleteAllMatchedBookCopiesLabel');
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
      applyToBothFormats: false
    };
  }

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

  onDeleteBookConfirmed = () => {
    const deleteFiles = this.state.deleteFiles;
    const addImportListExclusion = this.state.addImportListExclusion;
    const applyToBothFormats = this.state.applyToBothFormats;

    this.setState({ deleteFiles: false });
    this.setState({ addImportListExclusion: false });
    this.setState({ applyToBothFormats: false });
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
      bookId: 0,
      title,
      mediaType,
      files: []
    };
    const siblingDetails = siblingBook?.siblings ?? [];
    const visibleDeleteDetails = applyToBothFormats && siblingBook ?
      [currentBookDetail, ...siblingDetails] :
      [currentBookDetail];
    const siblingStatistics = siblingBook?.statistics ?? {};
    const currentFileCount = currentBookDetail.files?.length || bookFileCount;
    const currentSizeOnDisk = currentBookDetail.files?.length ? getFileSize([currentBookDetail]) : sizeOnDisk;
    const siblingFileCount = getFileCount(siblingDetails) || siblingStatistics.bookFileCount || 0;
    const siblingSizeOnDisk = getFileSize(siblingDetails) || siblingStatistics.sizeOnDisk || 0;
    const totalBookFileCount = currentFileCount + (applyToBothFormats ? siblingFileCount : 0);
    const totalSizeOnDisk = currentSizeOnDisk + (applyToBothFormats ? siblingSizeOnDisk : 0);

    const applyToBothBooks = applyToBothFormats && !!siblingBook;
    const deleteFilesLabel = translate(applyToBothBooks ?
      'DeleteFilesForAllMatchedBooksLabel' :
      'DeleteFilesForMediaTypeLabel', { mediaType: currentFormatLabel });
    const deleteFilesHelpText = translate(applyToBothBooks ?
      'DeleteFilesForAllMatchedBooksHelpText' :
      'DeleteFilesForMediaTypeHelpText', { mediaType: currentFormatLabel });
    const addImportListExclusionLabel = translate(applyToBothBooks ?
      'DontAddTheseBooksBackLabel' :
      'DontAddThisMediaTypeBackLabel', { mediaType: currentFormatLabel });
    const addImportListExclusionHelpText = translate(applyToBothBooks ?
      'DontAddTheseBooksBackHelpText' :
      'DontAddThisMediaTypeBackHelpText', { mediaType: currentFormatLabel });
    const siblingDeleteLabel = siblingBook ?
      getSiblingDeleteLabel(mediaType, siblingBook) :
      null;
    const siblingDeleteHelpText = siblingBook ?
      translate('DeleteFilesAndDontAddBackForMatchedCopiesHelpText', { mediaType: currentFormatLabel }) :
      null;

    return (
      <ModalContent
        onModalClose={onModalClose}
      >
        <ModalHeader>
          {translate('DeleteBookHeader', { title })}
        </ModalHeader>

        <ModalBody>

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

          {
            !!siblingBook &&
              <FormGroup>
                <FormLabel>{siblingDeleteLabel}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="applyToBothFormats"
                  value={applyToBothFormats}
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
                      translate('BooksMayBeAddedBackAfterRefreshOrSync') :
                      translate('BookMayBeAddedBackAfterRefreshOrSync')
                  }
                </div>
              </div>
          }

          {
            deleteFiles &&
              <div className={styles.deleteFilesMessage}>
                <div>
                  {translate(applyToBothFormats && siblingBook ? 'FilesForAllMatchedBooksWillBeDeleted' : 'TheBooksFilesWillBeDeleted')}
                </div>

                {
                  !!totalBookFileCount &&
                    <div>{translate('BookFilesTotalingSize', { count: totalBookFileCount, size: formatBytes(totalSizeOnDisk) })}</div>
                }

                {
                  visibleDeleteDetails.some((detail) => (detail.files ?? []).length) &&
                    <ul className={styles.fileList}>
                      {
                        visibleDeleteDetails.map((detail) => {
                          const files = detail.files ?? [];

                          return (
                            <li key={detail.bookId || detail.title} className={styles.fileListItem}>
                              <div>
                                {detail.title} ({formatMediaTypeLabel(detail.mediaType)})
                              </div>

                              {
                                files.length ?
                                  <ul className={styles.filePathList}>
                                    {
                                      files.map((file) => {
                                        return (
                                          <li key={file.path}>
                                            <span>{file.path}</span>
                                            <span className={styles.fileSize}>{formatBytes(file.size ?? 0)}</span>
                                          </li>
                                        );
                                      })
                                    }
                                  </ul> :
                                  <div className={styles.noFiles}>{translate('DeleteBookNoTrackedFiles')}</div>
                              }
                            </li>
                          );
                        })
                      }
                    </ul>
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
            onPress={this.onDeleteBookConfirmed}
          >
            {translate('Delete')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

DeleteBookModalContent.propTypes = {
  title: PropTypes.string.isRequired,
  mediaType: PropTypes.string,
  siblingBook: PropTypes.object,
  currentBookDeleteInfo: PropTypes.object,
  statistics: PropTypes.object.isRequired,
  onDeletePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

DeleteBookModalContent.defaultProps = {
  mediaType: 'audiobook',
  siblingBook: null,
  currentBookDeleteInfo: null,
  statistics: {
    bookFileCount: 0,
    sizeOnDisk: 0
  }
};

export default DeleteBookModalContent;
