import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TextTruncate from 'react-text-truncate';
import BookCover from 'Book/BookCover';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import AddAuthorOptionsForm from '../Common/AddAuthorOptionsForm.js';
import { getMissingAddFormatSetup } from '../Common/addFormatSetup.cjs';
import styles from './AddNewBookModalContent.css';

const MEDIA_TYPES = ['audiobook', 'ebook'];

function hasAddedMediaType(props, mediaType) {
  if (
    mediaType === 'audiobook' &&
    (props.localAudiobookBooks || []).length > 0
  ) {
    return true;
  }

  if (mediaType === 'ebook' && (props.localEbookBooks || []).length > 0) {
    return true;
  }

  return (props.addedMediaTypes || []).includes(mediaType);
}

function getMissingMediaTypes(props) {
  return MEDIA_TYPES.filter(
    (mediaType) => !hasAddedMediaType(props, mediaType)
  );
}

function getAmbiguousLocalBookCandidates(error) {
  const response = error?.responseJSON;
  if (response?.entityType !== 'book' || !Array.isArray(response.candidates)) {
    return [];
  }

  return response.candidates.filter(
    (candidate) => Number.isInteger(candidate?.id) && candidate.id > 0
  );
}

function canSelectBoth(props) {
  const initialMediaType = (props.initialMediaType ?? '').trim().toLowerCase();

  if (initialMediaType === 'audiobook' || initialMediaType === 'ebook') {
    return false;
  }

  return getMissingMediaTypes(props).length === MEDIA_TYPES.length;
}

function normalizeDefaultMediaType(value, allowBoth, missingMediaTypes) {
  const normalized = (value ?? '').trim().toLowerCase();

  if (normalized === 'audiobook' || normalized === 'ebook') {
    return missingMediaTypes.includes(normalized) ||
      missingMediaTypes.length !== 1 ?
      normalized :
      missingMediaTypes[0];
  }

  if (allowBoth && normalized === 'both') {
    return normalized;
  }

  return allowBoth ? 'both' : missingMediaTypes[0] || 'audiobook';
}

function getSelectedMediaType(props) {
  return normalizeDefaultMediaType(
    props.initialMediaType || props.defaultMediaType,
    canSelectBoth(props),
    getMissingMediaTypes(props)
  );
}

function getSelectedMediaTypeForAddedState(props, currentMediaType) {
  const missingMediaTypes = getMissingMediaTypes(props);

  if (!missingMediaTypes.length) {
    return currentMediaType;
  }

  if (currentMediaType === 'both') {
    return missingMediaTypes.length === MEDIA_TYPES.length ?
      'both' :
      missingMediaTypes[0];
  }

  return missingMediaTypes.includes(currentMediaType) ?
    currentMediaType :
    missingMediaTypes[0];
}

function canAddSelectedMediaType(props, mediaType) {
  if (mediaType === 'both') {
    return canSelectBoth(props);
  }

  return (
    MEDIA_TYPES.includes(mediaType) && !hasAddedMediaType(props, mediaType)
  );
}

function getMissingSetupForMediaType(props, mediaType) {
  if (!props.rootFoldersPopulated && !props.isExistingAuthor) {
    return ['loading'];
  }

  return getMissingAddFormatSetup({ ...props, selectedMediaType: mediaType });
}

function getAddButtonLabel(props, mediaType) {
  if (props.isQueued) {
    return translate('Queued');
  }

  const missingMediaTypes = getMissingMediaTypes(props);

  if (!missingMediaTypes.length) {
    return translate('BookBothFormatsAddedLabel');
  }

  if (!canAddSelectedMediaType(props, mediaType)) {
    return translate('BookAlreadyAddedLabel');
  }

  if (mediaType === 'both') {
    return translate('AddBothBookFormatsLabel');
  }

  return mediaType === 'ebook' ?
    translate('AddEbookBookLabel') :
    translate('AddAudiobookBookLabel');
}

class AddNewBookModalContent extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      searchForNewBook: false,
      selectedMediaType: getSelectedMediaType(props)
    };

    this._didUserSelectMediaType = false;
  }

  componentDidUpdate(prevProps) {
    if (prevProps.initialMediaType !== this.props.initialMediaType) {
      this._didUserSelectMediaType = false;
      this.setState({ selectedMediaType: getSelectedMediaType(this.props) });
      return;
    }

    if (
      prevProps.addFailedMediaType !== this.props.addFailedMediaType &&
      this.props.addFailedMediaType
    ) {
      this._didUserSelectMediaType = true;
      this.setState({ selectedMediaType: this.props.addFailedMediaType });
      return;
    }

    if (prevProps.addedMediaTypes !== this.props.addedMediaTypes) {
      const selectedMediaType = getSelectedMediaTypeForAddedState(
        this.props,
        this.state.selectedMediaType
      );

      if (selectedMediaType !== this.state.selectedMediaType) {
        this._didUserSelectMediaType = false;
        this.setState({ selectedMediaType });
        return;
      }
    }

    if (this._didUserSelectMediaType) {
      return;
    }

    if (prevProps.defaultMediaType !== this.props.defaultMediaType) {
      this.setState({ selectedMediaType: getSelectedMediaType(this.props) });
    }
  }

  //
  // Listeners

  onSearchForNewBookChange = ({ value }) => {
    this.setState({ searchForNewBook: value });
  };

  onMediaTypeChange = (mediaType) => {
    this._didUserSelectMediaType = true;
    this.setState({ selectedMediaType: mediaType });
  };

  onAddBookPress = () => {
    if (
      getMissingSetupForMediaType(this.props, this.state.selectedMediaType)
        .length > 0 ||
      !canAddSelectedMediaType(this.props, this.state.selectedMediaType)
    ) {
      return;
    }

    // Add the book without closing the modal; optionally trigger a search based on checkbox state.
    this.props.onAddBookPress(
      this.state.searchForNewBook,
      this.state.selectedMediaType
    );
  };

  onClosePress = () => {
    this.props.onModalClose();
  };

  //
  // Render

  render() {
    const {
      bookTitle,
      seriesTitle,
      authorName,
      disambiguation,
      overview,
      images,
      isAdding,
      isQueued,
      addNotice,
      isExistingAuthor,
      isSmallScreen,
      initialMediaType,
      defaultMediaType,
      addError,
      addedMediaTypes,
      addFailedMediaType,
      localAudiobookBooks,
      localEbookBooks,
      onSetDefaultMediaType,
      isSavingDefaultMediaType,
      onModalClose,
      ...otherProps
    } = this.props;

    const allowBothMediaType = canSelectBoth({
      initialMediaType,
      addedMediaTypes,
      localAudiobookBooks,
      localEbookBooks
    });
    const addErrorFallback = addFailedMediaType ?
      translate('CouldNotAddMediaType', {
        mediaType: translate(
          addFailedMediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel'
        )
      }) :
      translate('CouldNotAddThisBook');
    const addErrorMessage = addError ?
      getErrorMessage(addError, addError.message || addErrorFallback) :
      null;
    const ambiguousLocalBookCandidates = getAmbiguousLocalBookCandidates(addError);
    const missingAddSetup = getMissingSetupForMediaType(
      this.props,
      this.state.selectedMediaType
    );

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>{translate('AddNewBook')}</ModalHeader>

        <ModalBody>
          <div className={styles.container}>
            {isSmallScreen ? null : (
              <div className={styles.poster}>
                <BookCover
                  className={styles.poster}
                  images={images}
                  size={250}
                />
              </div>
            )}

            <div className={styles.info}>
              <div className={styles.name}>{bookTitle}</div>

              {!!disambiguation && (
                <span className={styles.disambiguation}>
                  ({disambiguation})
                </span>
              )}

              {!!seriesTitle && (
                <div className={styles.series}>{seriesTitle}</div>
              )}

              <div>
                <span className={styles.authorName}>
                  {' '}
                  {translate('ByAuthor', { authorName })}
                </span>
              </div>

              {overview ? (
                <div className={styles.overview}>
                  <TextTruncate
                    truncateText="…"
                    line={8}
                    text={stripHtml(overview)}
                  />
                </div>
              ) : null}

              <AddAuthorOptionsForm
                authorName={authorName}
                includeNoneMetadataProfile={true}
                includeSpecificBookMonitor={true}
                includeBothMediaType={allowBothMediaType}
                isExistingAuthor={isExistingAuthor}
                selectedMediaType={this.state.selectedMediaType}
                onMediaTypeChange={this.onMediaTypeChange}
                defaultMediaType={defaultMediaType}
                onSetDefaultMediaType={onSetDefaultMediaType}
                isSavingDefaultMediaType={isSavingDefaultMediaType}
                {...otherProps}
              />

              {isQueued && addNotice ? (
                <Alert className={styles.addError} kind={kinds.SUCCESS}>
                  {addNotice}
                </Alert>
              ) : null}

              {addErrorMessage ? (
                <Alert className={styles.addError} kind={kinds.DANGER}>
                  {addErrorMessage}
                  {ambiguousLocalBookCandidates.length ? (
                    <ul className={styles.ambiguousCandidates}>
                      {ambiguousLocalBookCandidates.map((candidate) => {
                        const details = [candidate.disambiguation, candidate.narrator]
                          .filter(Boolean)
                          .join(' — ');
                        const title = details ? `${candidate.title} (${details})` : candidate.title;
                        const format = translate(
                          candidate.mediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel'
                        );

                        return (
                          <li key={candidate.id}>
                            <Link
                              className={styles.ambiguousCandidateLink}
                              to={`/book/${candidate.id}`}
                              aria-label={translate('OpenFormatOfTitle', { format, title })}
                            >
                              {title}
                            </Link>
                          </li>
                        );
                      })}
                    </ul>
                  ) : null}
                </Alert>
              ) : null}
            </div>
          </div>
        </ModalBody>

        <ModalFooter className={styles.modalFooter}>
          <label className={styles.searchForNewBookLabelContainer}>
            <span className={styles.searchForNewBookLabel}>
              {translate('StartSearchForNewBook')}
            </span>

            <CheckInput
              containerClassName={styles.searchForNewBookContainer}
              className={styles.searchForNewBookInput}
              name="searchForNewBook"
              value={this.state.searchForNewBook}
              onChange={this.onSearchForNewBookChange}
            />
          </label>

          <div className={styles.buttons}>
            <SpinnerButton
              className={styles.addButton}
              kind={kinds.PRIMARY}
              isDisabled={
                isQueued ||
                missingAddSetup.length > 0 ||
                !canAddSelectedMediaType(
                  this.props,
                  this.state.selectedMediaType
                )
              }
              isSpinning={isAdding}
              onPress={this.onAddBookPress}
            >
              {getAddButtonLabel(this.props, this.state.selectedMediaType)}
            </SpinnerButton>

            <SpinnerButton
              className={styles.closeButton}
              kind={kinds.SUCCESS}
              onPress={this.onClosePress}
            >
              {translate('Close')}
            </SpinnerButton>
          </div>
        </ModalFooter>
      </ModalContent>
    );
  }
}

AddNewBookModalContent.propTypes = {
  bookTitle: PropTypes.string.isRequired,
  seriesTitle: PropTypes.string,
  authorName: PropTypes.string.isRequired,
  disambiguation: PropTypes.string,
  overview: PropTypes.string,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  isAdding: PropTypes.bool.isRequired,
  isQueued: PropTypes.bool.isRequired,
  addNotice: PropTypes.string,
  addError: PropTypes.object,
  addedMediaTypes: PropTypes.arrayOf(PropTypes.oneOf(['audiobook', 'ebook'])),
  addFailedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  isExistingAuthor: PropTypes.bool.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  rootFoldersPopulated: PropTypes.bool,
  initialMediaType: PropTypes.string,
  defaultMediaType: PropTypes.string,
  localAudiobookBooks: PropTypes.arrayOf(PropTypes.object),
  localEbookBooks: PropTypes.arrayOf(PropTypes.object),
  onSetDefaultMediaType: PropTypes.func,
  isSavingDefaultMediaType: PropTypes.bool,
  onModalClose: PropTypes.func.isRequired,
  onAddBookPress: PropTypes.func.isRequired
};

export default AddNewBookModalContent;
