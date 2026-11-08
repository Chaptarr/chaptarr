import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './DeleteAuthorModalContent.css';

class DeleteAuthorModalContent extends Component {
  constructor(props, context) {
    super(props, context);
    this.state = {
      mediaType: ['audiobook', 'ebook'].includes(props.selectedMediaType) ? props.selectedMediaType : '',
      applyToBothFormats: false,
      deleteFiles: false,
      addImportListExclusion: false,
      readdAuthor: false,
      recycleBin: null,
      preview: null,
      previewError: null,
      deleteOtherAuthorsFiles: false
    };
  }

  componentWillUnmount() {
    this.previewGeneration = (this.previewGeneration || 0) + 1;
    this.abortPreviewRequest?.();
  }

  componentDidUpdate(prevProps, prevState) {
    if (this.state.deleteFiles !== prevState.deleteFiles ||
        this.state.mediaType !== prevState.mediaType ||
        this.state.applyToBothFormats !== prevState.applyToBothFormats) {
      this.loadPreview();
    }
  }

  loadPreview = () => {
    this.abortPreviewRequest?.();
    const generation = (this.previewGeneration || 0) + 1;
    this.previewGeneration = generation;
    this.setState({ preview: null, previewError: null, deleteOtherAuthorsFiles: false });
    const { mediaType, applyToBothFormats, deleteFiles } = this.state;
    if (!deleteFiles || (!mediaType && !applyToBothFormats)) {
      return;
    }

    const { request, abortRequest } = createAjaxRequest({
      url: '/author/deletepreview', method: 'POST', dataType: 'json',
      data: JSON.stringify({ authorIds: this.props.authors.map((author) => author.id),
        mediaType: applyToBothFormats ? undefined : mediaType, applyToBothFormats })
    });
    this.abortPreviewRequest = abortRequest;
    request.done((preview) => {
      if (generation === this.previewGeneration) {
        this.setState({ preview, recycleBin: preview.recycleBin });
      }
    }).fail((error) => {
      if (!error.aborted && generation === this.previewGeneration) {
        this.setState({ previewError: getErrorMessage(error, translate('UnableToLoadDeletionFiles')) });
      }
    });
  };

  onChange = ({ name, value }) => {
    this.setState((state) => ({
      [name]: value,
      ...(name === 'mediaType' ? {
        mediaType: value === 'both' ? state.mediaType : value,
        applyToBothFormats: value === 'both',
        readdAuthor: value === 'both' && state.readdAuthor
      } : {}),
      ...(name === 'deleteFiles' && value ? { readdAuthor: false } : {}),
      ...(name === 'readdAuthor' && value ? { applyToBothFormats: true, addImportListExclusion: false } : {})
    }));
  };

  onDeleteAuthorConfirmed = () => {
    const { mediaType, applyToBothFormats, deleteFiles, addImportListExclusion, readdAuthor } = this.state;
    if (deleteFiles && (!this.state.preview || this.state.previewError ||
        (this.state.preview.otherAuthors.length > 0 && !this.state.deleteOtherAuthorsFiles))) {
      return;
    }
    const options = { mediaType, applyToBothFormats, deleteFiles, addImportListExclusion, readdAuthor,
      ...(this.state.deleteOtherAuthorsFiles ? { deleteOtherAuthorsFiles: true } : {}) };
    this.props.onDeletePress({ ...options, mediaType: options.applyToBothFormats ? undefined : options.mediaType || undefined });
  };

  render() {
    const { authors, allowReadd, isDeleting, deleteError, onModalClose } = this.props;
    if (!authors.length) {
      return null;
    }

    const { mediaType, applyToBothFormats, deleteFiles, addImportListExclusion, readdAuthor, recycleBin, preview, previewError, deleteOtherAuthorsFiles } = this.state;
    const formatName = mediaType === 'ebook' ? translate('Ebooks') : translate('Audiobooks');
    const scopeChosen = applyToBothFormats || !!mediaType;
    const scopeLabel = applyToBothFormats ? translate('AuthorDeleteBothFormats') : formatName;
    const calibrePossible = applyToBothFormats || mediaType === 'ebook';
    let disposalMessage = 'AuthorDeleteRecyclingBinUnknown';
    if (recycleBin !== null) {
      disposalMessage = recycleBin ? 'ConfirmImportRecyclingBin' : 'ConfirmImportPermanentDeletion';
      if (calibrePossible) {
        disposalMessage = recycleBin ? 'DeleteBookFilesCalibreRecyclingWarning' : 'DeleteBookFilesCalibrePermanentWarning';
      }
    }

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {allowReadd ? translate('DeleteAuthorHeader', { authorName: authors[0].authorName }) : translate('DeleteSelectedAuthor')}
        </ModalHeader>
        <ModalBody>
          {deleteError && <Alert kind={kinds.DANGER}>{getErrorMessage(deleteError, translate('AuthorDeleteFailed'))}</Alert>}
          <FormGroup>
            <FormLabel>{translate('AuthorDeleteFormat')}</FormLabel>
            <FormInputGroup
              type={inputTypes.SELECT}
              name="mediaType"
              value={applyToBothFormats ? 'both' : mediaType}
              values={[
                { key: '', value: translate('AuthorDeleteSelectFormat'), isDisabled: true },
                { key: 'audiobook', value: translate('Audiobooks') },
                { key: 'ebook', value: translate('Ebooks') },
                { key: 'both', value: translate('AuthorDeleteBothFormats') }
              ]}
              isDisabled={isDeleting}
              onChange={this.onChange}
            />
          </FormGroup>
          <FormGroup>
            <FormLabel>{applyToBothFormats ? translate('AuthorDeleteFolders') : translate('AuthorDeleteFormatFiles', { mediaType: scopeChosen ? formatName : translate('AuthorDeleteFormat') })}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="deleteFiles"
              value={deleteFiles}
              isDisabled={isDeleting || readdAuthor}
              helpText={translate(applyToBothFormats ? 'AuthorDeleteFoldersHelpText' : 'AuthorDeleteFormatFilesHelpText')}
              kind={kinds.DANGER}
              onChange={this.onChange}
            />
          </FormGroup>
          {deleteFiles && scopeChosen &&
            <Alert kind={recycleBin && !calibrePossible ? kinds.INFO : kinds.WARNING}>
              {translate(disposalMessage)}
            </Alert>
          }
          {deleteFiles && scopeChosen &&
            <>
              {!preview && !previewError && <LoadingIndicator />}
              {previewError && <Alert kind={kinds.DANGER}>{previewError}</Alert>}
              {preview?.warnings.map((warning) => <Alert key={warning} kind={kinds.WARNING}>{warning}</Alert>)}
              {preview?.folders.map((folder) => (
                <div key={folder.path} className={styles.pathContainer}>
                  <div>{folder.path}</div>
                  <div>{folder.deleteContents ? translate('AuthorDeleteWholeFolder') : folder.reason}</div>
                  <div>{translate('BookFilesTotalingSize', { count: folder.bookFileCount, size: formatBytes(folder.size) })}</div>
                  {folder.otherFiles.length > 0 &&
                    <details>
                      <summary>{translate('AuthorDeleteOtherFiles', { count: folder.otherFiles.length })}</summary>
                      <ul>{folder.otherFiles.map((path) => <li className={styles.pathContainer} key={path}>{path}</li>)}</ul>
                    </details>
                  }
                </div>
              ))}
              {preview &&
                <ul>{preview.files.map((file) => (
                  <li className={styles.pathContainer} key={file.path}>{file.path}{file.reason && <div>{translate('AuthorDeleteKeptFile', { reason: file.reason })}</div>}</li>
                ))}</ul>
              }
              {preview?.otherAuthors.length > 0 &&
                <FormGroup>
                  <FormLabel>{translate('AuthorDeleteOtherAuthors')}</FormLabel>
                  <FormInputGroup type={inputTypes.CHECK} name="deleteOtherAuthorsFiles"
                    value={deleteOtherAuthorsFiles} onChange={this.onChange} kind={kinds.DANGER}
                    helpText={preview.otherAuthors.map((author) => translate('AuthorDeleteOtherAuthorWarning', { author: author.name, count: author.fileCount })).join(' ')}
                  />
                </FormGroup>
              }
            </>
          }
          <FormGroup>
            <FormLabel>{translate('AddListExclusion')}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="addImportListExclusion"
              value={addImportListExclusion}
              isDisabled={isDeleting || readdAuthor}
              helpText={translate('AuthorDeleteExclusionHelpText', { mediaType: scopeChosen ? scopeLabel : translate('AuthorDeleteFormat') })}
              kind={kinds.DANGER}
              onChange={this.onChange}
            />
          </FormGroup>
          {allowReadd &&
            <FormGroup>
              <FormLabel>{translate('PurgeAndReaddAuthor')}</FormLabel>
              <FormInputGroup
                type={inputTypes.CHECK}
                name="readdAuthor"
                value={readdAuthor}
                isDisabled={isDeleting || deleteFiles}
                helpText={translate('PurgeAndReaddAuthorHelpText')}
                kind={kinds.DANGER}
                onChange={this.onChange}
              />
            </FormGroup>
          }
          <ul>
            {authors.map((author) => (
              <li key={author.id}>
                {author.authorName}
                {deleteFiles && !applyToBothFormats && scopeChosen &&
                  <div className={styles.pathContainer}>
                    {translate('AuthorDeleteScopedFilesSummary', { mediaType: formatName })}
                  </div>
                }
              </li>
            ))}
          </ul>
        </ModalBody>
        <ModalFooter>
          <Button onPress={onModalClose} isDisabled={isDeleting}>{translate('Cancel')}</Button>
          <Button kind={kinds.DANGER} onPress={this.onDeleteAuthorConfirmed}
            isDisabled={isDeleting || !scopeChosen || (deleteFiles && (!preview || !!previewError || (preview.otherAuthors.length > 0 && !deleteOtherAuthorsFiles)))}
          >
            {readdAuthor ? translate('PurgeAndReaddAuthor') : translate('Delete')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

DeleteAuthorModalContent.propTypes = {
  authors: PropTypes.arrayOf(PropTypes.object).isRequired,
  allowReadd: PropTypes.bool,
  selectedMediaType: PropTypes.string,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  onDeletePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default DeleteAuthorModalContent;
