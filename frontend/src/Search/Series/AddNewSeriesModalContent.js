import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, kinds, sizes } from 'Helpers/Props';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import AddAuthorOptionsForm from '../Common/AddAuthorOptionsForm.js';
import styles from './AddNewSeriesModalContent.css';

function normalizeDefaultMediaType(value, allowBoth) {
  const normalized = (value ?? '').trim().toLowerCase();

  if (normalized === 'audiobook' || normalized === 'ebook') {
    return normalized;
  }

  if (allowBoth && normalized === 'both') {
    return normalized;
  }

  return null;
}

class AddNewSeriesModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      selectedMediaType: normalizeDefaultMediaType(props.defaultMediaType, true) ?? 'audiobook',
      selectedBookIds: [],
      initializedSeriesId: null
    };

    this._didUserSelectMediaType = false;
  }

  componentDidUpdate(prevProps) {
    if (!this._didUserSelectMediaType && prevProps.defaultMediaType !== this.props.defaultMediaType) {
      const normalized = normalizeDefaultMediaType(this.props.defaultMediaType, true);
      if (normalized) {
        this.setState({ selectedMediaType: normalized });
      }
    }

    const { seriesDetails, foreignSeriesId } = this.props;
    const seriesDetailsId = seriesDetails && seriesDetails.foreignSeriesId;

    // When switching to a different series, reset initialization so selection doesn't leak across modals.
    if (prevProps.foreignSeriesId !== foreignSeriesId && this.state.initializedSeriesId !== null) {
      this.setState({ selectedBookIds: [], initializedSeriesId: null });
      return;
    }

    // Initialize selection once per series after details arrive.
    if (
      seriesDetails &&
      seriesDetails.books &&
      seriesDetails.books.length > 0 &&
      seriesDetailsId === foreignSeriesId &&
      this.state.initializedSeriesId !== foreignSeriesId
    ) {
      this.setState({
        selectedBookIds: seriesDetails.books
          .map((b) => b.foreignBookId)
          .filter((id) => !!id),
        initializedSeriesId: foreignSeriesId
      });
    }
  }

  //
  // Listeners

  onAddSelectedPress = () => {
    const { onAddSeriesPress, seriesDetails } = this.props;
    const { selectedBookIds, selectedMediaType } = this.state;

    if (!seriesDetails || !seriesDetails.books || seriesDetails.books.length === 0) {
      return;
    }

    const selectedBooks = seriesDetails.books
      .filter((b) => selectedBookIds.includes(b.foreignBookId))
      .map((b) => ({
        foreignBookId: b.foreignBookId,
        foreignAuthorId: b.foreignAuthorId
      }));

    if (!selectedBooks.length) {
      return;
    }

    onAddSeriesPress({
      selectedMediaType,
      selectedBooks
    });
  };

  onAddAllPress = () => {
    const { seriesDetails } = this.props;

    if (!seriesDetails || !seriesDetails.books || seriesDetails.books.length === 0) {
      return;
    }

    const allIds = seriesDetails.books
      .map((b) => b.foreignBookId)
      .filter((id) => !!id);

    this.setState({ selectedBookIds: allIds }, () => {
      this.onAddSelectedPress();
    });
  };

  onMediaTypeChange = (mediaType) => {
    this._didUserSelectMediaType = true;
    this.setState({ selectedMediaType: mediaType });
  };

  onBookToggle = (bookId) => {
    this.setState((prevState) => {
      const isSelected = prevState.selectedBookIds.includes(bookId);
      const selectedBookIds = isSelected ?
        prevState.selectedBookIds.filter((id) => id !== bookId) :
        [...prevState.selectedBookIds, bookId];

      return { selectedBookIds };
    });
  };

  //
  // Helpers

  getValidYear(releaseDate) {
    if (!releaseDate) {
      return null;
    }

    const year = new Date(releaseDate).getFullYear();
    const currentYear = new Date().getFullYear();

    // Same validation as backend: years before 1450 or after current year are invalid
    if (year < 1450 || year > currentYear) {
      return null;
    }

    return year;
  }

  //
  // Render

  render() {
    const {
      foreignSeriesId,
      title,
      titleSlug,
      description,
      workCount,
      primaryWorkCount,
      isAdding,
      isAddingSeries,
      addError,
      error,
      isFetching,
      seriesDetails,
      fetchError,
      onModalClose,
      onInputChange,
      defaultMediaType,
      onSetDefaultMediaType,
      isSavingDefaultMediaType,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      rootFoldersPopulated,
      showMetadataProfile,
      isWindows,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      monitor,
      audiobookMonitored,
      ebookMonitored,
      audiobookMonitor,
      ebookMonitor,
      monitorNewItems,
      audiobookMonitorNewItems,
      ebookMonitorNewItems,
      tags,
      audiobookTags,
      ebookTags
    } = this.props;

    const { selectedMediaType, selectedBookIds } = this.state;
    const isSubmitting = isAdding || isAddingSeries;

    const audiobookRoot = audiobookRootFolderPath?.value;
    const ebookRoot = ebookRootFolderPath?.value;
    const audiobookProfile = audiobookQualityProfileId?.value;
    const ebookProfile = ebookQualityProfileId?.value;

    const needsAudiobook = selectedMediaType === 'audiobook' || selectedMediaType === 'both';
    const needsEbook = selectedMediaType === 'ebook' || selectedMediaType === 'both';

    const isAddDisabled = !rootFoldersPopulated ||
      (needsAudiobook && (!audiobookRoot || !audiobookProfile || audiobookProfile === 0 || audiobookProfile === 'none')) ||
      (needsEbook && (!ebookRoot || !ebookProfile || ebookProfile === 0 || ebookProfile === 'none'));

    const hasBooks = !!(seriesDetails && seriesDetails.books && seriesDetails.books.length > 0);
    const selectedCount = hasBooks ?
      seriesDetails.books.filter((b) => selectedBookIds.includes(b.foreignBookId)).length :
      0;
    const seriesWorkCount = seriesDetails && Number.isInteger(seriesDetails.workCount) ? seriesDetails.workCount : null;
    const seriesPrimaryWorkCount = seriesDetails && Number.isInteger(seriesDetails.primaryWorkCount) ? seriesDetails.primaryWorkCount : null;
    const displayWorkCount = seriesWorkCount != null ? seriesWorkCount : workCount;
    const displayPrimaryWorkCount = seriesPrimaryWorkCount != null ? seriesPrimaryWorkCount : primaryWorkCount;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('AddNewSeries')} - {title}
        </ModalHeader>

        <ModalBody>
          <div className={styles.container}>
            <div className={styles.settings}>
              <AddAuthorOptionsForm
                includeNoneMetadataProfile={false}
                includeSpecificBookMonitor={false}
                includeBothMediaType={true}
                selectedMediaType={selectedMediaType}
                onMediaTypeChange={this.onMediaTypeChange}
                defaultMediaType={defaultMediaType}
                onSetDefaultMediaType={onSetDefaultMediaType}
                isSavingDefaultMediaType={isSavingDefaultMediaType}
                showMetadataProfile={showMetadataProfile}
                audiobookRootFolderPath={audiobookRootFolderPath}
                ebookRootFolderPath={ebookRootFolderPath}
                audiobookQualityProfileId={audiobookQualityProfileId}
                ebookQualityProfileId={ebookQualityProfileId}
                audiobookMetadataProfileId={audiobookMetadataProfileId}
                ebookMetadataProfileId={ebookMetadataProfileId}
                monitor={monitor}
                audiobookMonitored={audiobookMonitored}
                ebookMonitored={ebookMonitored}
                audiobookMonitor={audiobookMonitor}
                ebookMonitor={ebookMonitor}
                monitorNewItems={monitorNewItems}
                audiobookMonitorNewItems={audiobookMonitorNewItems}
                ebookMonitorNewItems={ebookMonitorNewItems}
                tags={tags}
                audiobookTags={audiobookTags}
                ebookTags={ebookTags}
                folder=""
                isWindows={isWindows}
                onInputChange={onInputChange}
              />
            </div>

            <div className={styles.info}>
              <div className={styles.title}>
                {title}
              </div>

              <div className={styles.bookCountAndButton}>
                <Label size={sizes.LARGE}>
                  {displayWorkCount} {translate(displayWorkCount === 1 ? 'Book' : 'Books')}
                  {displayPrimaryWorkCount !== displayWorkCount && ` (${displayPrimaryWorkCount} Primary)`}
                  {hasBooks && ` • ${selectedCount} Selected`}
                </Label>
                <div className={styles.addButtons}>
                  <SpinnerButton
                    kind={kinds.PRIMARY}
                    size={sizes.MEDIUM}
                    isSpinning={isSubmitting}
                    isDisabled={isSubmitting || isFetching || isAddDisabled || !hasBooks || selectedCount === 0}
                    onPress={this.onAddSelectedPress}
                    title={`Add all authors from "${title}" and monitor the selected books.`}
                  >
                    <Icon name={icons.ADD} />
                    {' '}
                    {translate('AddSelected')}
                  </SpinnerButton>

                  <SpinnerButton
                    kind={kinds.DEFAULT}
                    size={sizes.MEDIUM}
                    isSpinning={isSubmitting}
                    isDisabled={isSubmitting || isFetching || isAddDisabled || !hasBooks}
                    onPress={this.onAddAllPress}
                    title={`Add all authors from "${title}" and monitor all books in this series.`}
                  >
                    <Icon name={icons.ADD} />
                    {' '}
                    {translate('AddAllToLibrary')}
                  </SpinnerButton>
                </div>
              </div>

              {isFetching && (
                <div>
                  <LoadingIndicator />
                  <div>{translate('LoadingSeriesDetails')}</div>
                </div>
              )}

              {fetchError && (
                <Alert kind={kinds.WARNING}>
                  {fetchError}
                </Alert>
              )}

              {addError && (
                <Alert kind={kinds.DANGER}>
                  {addError}
                </Alert>
              )}

              {seriesDetails && seriesDetails.books && seriesDetails.books.length > 0 && (
                <div className={styles.booksSection}>
                  <h3 className={styles.booksSectionTitle}>{translate('BooksInThisSeries')}</h3>
                  <div className={styles.booksList}>
                    {seriesDetails.books.map((book, index) => {
                      const authorName = book.authorName || book.author?.authorName;
                      const overview = book.overview ? stripHtml(book.overview) : '';
                      const validYear = this.getValidYear(book.releaseDate);

                      return (
                        <div
                          key={book.foreignBookId || index}
                          className={`${styles.bookItem} ${selectedBookIds.includes(book.foreignBookId) ? styles.bookItemSelected : ''}`}
                          onClick={() => this.onBookToggle(book.foreignBookId)}
                        >
                          <div className={styles.bookSelect}>
                            <input
                              type="checkbox"
                              checked={selectedBookIds.includes(book.foreignBookId)}
                              onChange={() => this.onBookToggle(book.foreignBookId)}
                              onClick={(e) => e.stopPropagation()}
                              disabled={isSubmitting}
                            />
                          </div>
                          <div className={styles.bookCover}>
                            {book.images && book.images.length > 0 && (
                              <img
                                src={book.images[0].url}
                                alt={book.title}
                                className={styles.bookCoverImage}
                              />
                            )}
                          </div>
                          <div className={styles.bookInfo}>
                            <div className={styles.bookTitle}>
                              {book.position && <span className={styles.bookPosition}>{book.position}. </span>}
                              {book.title}
                            </div>
                            {authorName && <div className={styles.bookAuthorName}>{authorName}</div>}
                            <div className={styles.bookMetadata}>
                              {validYear && (
                                <span className={styles.bookReleaseDate}>
                                  {validYear}
                                </span>
                              )}
                              {validYear && book.ratings && book.ratings.value > 0 && (
                                <span className={styles.metadataSeparator}> • </span>
                              )}
                              {book.ratings && book.ratings.value > 0 && (
                                <span className={styles.bookRating}>
                                  ★ {translate('RatingValueAndVotes', { value: book.ratings.value.toFixed(1), votes: book.ratings.votes })}
                                </span>
                              )}
                            </div>
                            {!!overview && <div className={styles.bookOverview}>{overview}</div>}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}

              {(!seriesDetails || !seriesDetails.books || seriesDetails.books.length === 0) && !isFetching && (
                <div className={styles.noBooksMessage}>
                  <p>{translate('NoBookDetailsForSeriesPreview')}</p>
                </div>
              )}

              {error && (
                <Alert kind={kinds.DANGER}>
                  {translate('SeriesAddError')}
                  {error.message || error}
                </Alert>
              )}
            </div>
          </div>
        </ModalBody>

        <ModalFooter>
          <Button
            className={styles.cancelButton}
            kind={kinds.DEFAULT}
            onPress={onModalClose}
          >
            {translate('Cancel')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
	  }
}

AddNewSeriesModalContent.propTypes = {
  foreignSeriesId: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  titleSlug: PropTypes.string,
  description: PropTypes.string,
  workCount: PropTypes.number,
  primaryWorkCount: PropTypes.number,
  isAdding: PropTypes.bool.isRequired,
  isAddingSeries: PropTypes.bool,
  addError: PropTypes.string,
  error: PropTypes.object,
  isFetching: PropTypes.bool.isRequired,
  isSavingDefaultMediaType: PropTypes.bool,
  seriesDetails: PropTypes.object,
  fetchError: PropTypes.string,
  defaultMediaType: PropTypes.string,
  rootFoldersPopulated: PropTypes.bool,
  showMetadataProfile: PropTypes.bool,
  isWindows: PropTypes.bool,
  onSetDefaultMediaType: PropTypes.func,
  onAddSeriesPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired,
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  audiobookQualityProfileId: PropTypes.object,
  ebookQualityProfileId: PropTypes.object,
  audiobookMetadataProfileId: PropTypes.object,
  ebookMetadataProfileId: PropTypes.object,
  monitor: PropTypes.object,
  audiobookMonitored: PropTypes.object,
  ebookMonitored: PropTypes.object,
  audiobookMonitor: PropTypes.object,
  ebookMonitor: PropTypes.object,
  monitorNewItems: PropTypes.object,
  audiobookMonitorNewItems: PropTypes.object,
  ebookMonitorNewItems: PropTypes.object,
  tags: PropTypes.object,
  audiobookTags: PropTypes.object,
  ebookTags: PropTypes.object
};

AddNewSeriesModalContent.defaultProps = {
  workCount: 0,
  primaryWorkCount: 0,
  isAdding: false,
  isAddingSeries: false,
  isFetching: false,
  isSavingDefaultMediaType: false,
  rootFoldersPopulated: false,
  showMetadataProfile: true,
  isWindows: false
};

export default AddNewSeriesModalContent;
