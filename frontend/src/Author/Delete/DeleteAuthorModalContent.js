import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
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
      readdAuthor: false
    };
  }

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
    this.props.onDeletePress({ ...this.state, mediaType: this.state.applyToBothFormats ? undefined : this.state.mediaType || undefined });
  };

  render() {
    const { authors, allowReadd, isDeleting, deleteError, onModalClose } = this.props;
    if (!authors.length) {
      return null;
    }

    const { mediaType, applyToBothFormats, deleteFiles, addImportListExclusion, readdAuthor } = this.state;
    const formatName = mediaType === 'ebook' ? translate('Ebooks') : translate('Audiobooks');
    const scopeChosen = applyToBothFormats || !!mediaType;
    const scopeLabel = applyToBothFormats ? translate('AuthorDeleteBothFormats') : formatName;

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
                {deleteFiles && [...new Set([author.path, author.audiobookFolder, author.ebookFolder].filter(Boolean))].map((path) => (
                  <div className={styles.pathContainer} key={path}>{path}</div>
                ))}
              </li>
            ))}
          </ul>
        </ModalBody>
        <ModalFooter>
          <Button onPress={onModalClose} isDisabled={isDeleting}>{translate('Cancel')}</Button>
          <Button kind={kinds.DANGER} onPress={this.onDeleteAuthorConfirmed}
            isDisabled={isDeleting || !scopeChosen}
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
