import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorNameLink from 'Author/AuthorNameLink';
import DeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import EditAuthorModalConnector from 'Author/Edit/EditAuthorModalConnector';
import BookNameLink from 'Book/BookNameLink';
import EditBookModalConnector from 'Book/Edit/EditBookModalConnector';
import NarratorCell from 'Components/Book/NarratorCell';
import HeartRating from 'Components/HeartRating';
import IconButton from 'Components/Link/IconButton';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import VirtualTableSelectCell from 'Components/Table/Cells/VirtualTableSelectCell';
import TagListConnector from 'Components/TagListConnector';
import { icons } from 'Helpers/Props';
import formatDurationMinutes from 'Utilities/Date/formatDurationMinutes';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import BookStatusCell from './BookStatusCell';
import styles from './BookIndexRow.css';

class BookIndexRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      hasBannerError: false,
      isEditAuthorModalOpen: false,
      isDeleteAuthorModalOpen: false,
      isEditBookModalOpen: false
    };
  }

  onEditAuthorPress = () => {
    this.setState({ isEditAuthorModalOpen: true });
  };

  onEditAuthorModalClose = () => {
    this.setState({ isEditAuthorModalOpen: false });
  };

  onDeleteAuthorPress = () => {
    this.setState({
      isEditAuthorModalOpen: false,
      isDeleteAuthorModalOpen: true
    });
  };

  onDeleteAuthorModalClose = () => {
    this.setState({ isDeleteAuthorModalOpen: false });
  };

  onEditBookPress = () => {
    this.setState({ isEditBookModalOpen: true });
  };

  onEditBookModalClose = () => {
    this.setState({ isEditBookModalOpen: false });
  };

  onUseSceneNumberingChange = () => {
    // Mock handler to satisfy `onChange` being required for `CheckInput`.
    //
  };

  onBannerLoad = () => {
    if (this.state.hasBannerError) {
      this.setState({ hasBannerError: false });
    }
  };

  onBannerLoadError = () => {
    if (!this.state.hasBannerError) {
      this.setState({ hasBannerError: true });
    }
  };

  //
  // Render

  render() {
    const {
      id,
      authorId,
      audiobookMonitored,
      ebookMonitored,
      hasFiles,
      anyEditionOk,
      selectedMediaType,
      title,
      author,
      authorTitle,
      narrator,
      narratorNames,
      availableNarrators,
      durationMinutes,
      titleSlug,
      localBookId,
      qualityProfile,
      releaseDate,
      added,
      statistics,
      genres,
      grabbed,
      ratings,
      tags,
      showSearchAction,
      columns,
      editions,
      isRefreshingBook,
      isSearchingBook,
      isEditorActive,
      isSelected,
      onRefreshBookPress,
      onSearchPress,
      onSelectedChange
    } = this.props;

    const hasNarrator = narrator && (typeof narrator !== 'string' || narrator.trim().length > 0);
    const showNarratorPicker = selectedMediaType === 'audiobook';

    const {
      bookFileCount,
      sizeOnDisk
    } = statistics;

    const hasFilesForDisplay = (typeof hasFiles === 'boolean') ? hasFiles : bookFileCount > 0;
    const hasPinnedEdition = (anyEditionOk === false) || (Array.isArray(editions) && editions.some((e) => e?.manualAdd === true));
    const canDisplayAudioMetadata = hasFilesForDisplay || hasPinnedEdition;

    const {
      isEditAuthorModalOpen,
      isDeleteAuthorModalOpen,
      isEditBookModalOpen
    } = this.state;

    return (
      <>
        {
          columns.map((column) => {
            const {
              name,
              isVisible
            } = column;

            if (!isVisible) {
              return null;
            }

            if (isEditorActive && name === 'select') {
              return (
                <VirtualTableSelectCell
                  inputClassName={styles.checkInput}
                  id={id}
                  key={name}
                  isSelected={isSelected}
                  isDisabled={false}
                  onSelectedChange={onSelectedChange}
                />
              );
            }

            if (name === 'status') {
              return (
                <BookStatusCell
                  key={name}
                  className={styles[name]}
                  grabbed={grabbed}
                  monitored={audiobookMonitored || ebookMonitored}
                  component={VirtualTableRowCell}
                />
              );
            }

            if (name === 'title') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={classNames(
                    styles[name]
                  )}
                >
                  <BookNameLink
                    titleSlug={titleSlug}
                    localBookId={localBookId}
                    title={title}
                  />
                </VirtualTableRowCell>
              );
            }

            if (name === 'authorName') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={classNames(
                    styles[name]
                  )}
                >
                  <AuthorNameLink
                    authorId={author?.id ?? authorId}
                    authorName={author?.authorName ?? authorTitle ?? ''}
                  />
                </VirtualTableRowCell>
              );
            }

            if (name === 'narrator') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {statistics.bookFileCount > 0 || hasNarrator || showNarratorPicker ? (
                    <NarratorCell
                      bookId={id}
                      narrator={narrator}
                      narratorNames={narratorNames}
                      availableNarrators={availableNarrators}
                      hasFiles={hasFilesForDisplay}
                      hasPinnedEdition={hasPinnedEdition}
                      authorName={author?.authorName ?? authorTitle ?? ''}
                      bookTitle={title}
                    />
                  ) : null}
                </VirtualTableRowCell>
              );
            }

            if (name === 'duration') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {(showNarratorPicker && canDisplayAudioMetadata && durationMinutes) ? formatDurationMinutes(durationMinutes) : ''}
                </VirtualTableRowCell>
              );
            }

            if (name === 'qualityProfileId') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {qualityProfile?.name ?? ''}
                </VirtualTableRowCell>
              );
            }

            if (name === 'releaseDate') {
              return (
                <RelativeDateCellConnector
                  key={name}
                  className={styles[name]}
                  date={releaseDate}
                  component={VirtualTableRowCell}
                  timeForToday={false}
                />
              );
            }

            if (name === 'added') {
              return (
                <RelativeDateCellConnector
                  key={name}
                  className={styles[name]}
                  date={added}
                  component={VirtualTableRowCell}
                />
              );
            }

            if (name === 'bookFileCount') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {bookFileCount}
                </VirtualTableRowCell>
              );
            }

            if (name === 'path') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {author?.path ?? ''}
                </VirtualTableRowCell>
              );
            }

            if (name === 'sizeOnDisk') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  {formatBytes(sizeOnDisk)}
                </VirtualTableRowCell>
              );
            }

            if (name === 'genres') {
              const joinedGenres = genres.join(', ');

              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  <span title={joinedGenres}>
                    {joinedGenres}
                  </span>
                </VirtualTableRowCell>
              );
            }

            if (name === 'ratings') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  <HeartRating
                    rating={ratings.value}
                  />
                </VirtualTableRowCell>
              );
            }

            if (name === 'tags') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  <TagListConnector
                    tags={tags}
                  />
                </VirtualTableRowCell>
              );
            }

            if (name === 'actions') {
              return (
                <VirtualTableRowCell
                  key={name}
                  className={styles[name]}
                >
                  <SpinnerIconButton
                    name={icons.REFRESH}
                    title={translate('RefreshBook')}
                    isSpinning={isRefreshingBook}
                    onPress={onRefreshBookPress}
                  />

                  {
                    showSearchAction &&
                      <SpinnerIconButton
                        className={styles.action}
                        name={icons.SEARCH}
                        title={translate('SearchForMonitoredBooks')}
                        isSpinning={isSearchingBook}
                        onPress={onSearchPress}
                      />
                  }

                  <IconButton
                    name={icons.INTERACTIVE}
                    title={translate('EditAuthor')}
                    onPress={this.onEditAuthorPress}
                  />

                  <IconButton
                    className={styles.action}
                    name={icons.EDIT}
                    title={translate('EditBook')}
                    onPress={this.onEditBookPress}
                  />
                </VirtualTableRowCell>
              );
            }

            return null;
          })
        }

        <EditAuthorModalConnector
          isOpen={isEditAuthorModalOpen}
          authorId={authorId}
          onModalClose={this.onEditAuthorModalClose}
          onDeleteAuthorPress={this.onDeleteAuthorPress}
        />

        <DeleteAuthorModal
          isOpen={isDeleteAuthorModalOpen}
          authorId={authorId}
          onModalClose={this.onDeleteAuthorModalClose}
        />

        <EditBookModalConnector
          isOpen={isEditBookModalOpen}
          authorId={authorId}
          bookId={id}
          onModalClose={this.onEditBookModalClose}
        />
      </>
    );
  }
}

BookIndexRow.propTypes = {
  id: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  audiobookMonitored: PropTypes.bool,
  ebookMonitored: PropTypes.bool,
  anyEditionOk: PropTypes.bool,
  hasFiles: PropTypes.bool,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  title: PropTypes.string.isRequired,
  titleSlug: PropTypes.string.isRequired,
  localBookId: PropTypes.string,
  author: PropTypes.object,
  authorTitle: PropTypes.string,
  narrator: PropTypes.string,
  narratorNames: PropTypes.arrayOf(PropTypes.string),
  availableNarrators: PropTypes.arrayOf(PropTypes.string),
  duration: PropTypes.string,
  durationMinutes: PropTypes.number,
  qualityProfile: PropTypes.object.isRequired,
  releaseDate: PropTypes.string,
  added: PropTypes.string,
  statistics: PropTypes.object.isRequired,
  genres: PropTypes.arrayOf(PropTypes.string).isRequired,
  grabbed: PropTypes.bool,
  ratings: PropTypes.object.isRequired,
  tags: PropTypes.arrayOf(PropTypes.number).isRequired,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  editions: PropTypes.arrayOf(PropTypes.object),
  showSearchAction: PropTypes.bool.isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  isRefreshingBook: PropTypes.bool.isRequired,
  isSearchingBook: PropTypes.bool.isRequired,
  onRefreshBookPress: PropTypes.func.isRequired,
  onSearchPress: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

BookIndexRow.defaultProps = {
  statistics: {
    bookCount: 0,
    bookFileCount: 0,
    totalBookCount: 0
  },
  genres: [],
  tags: []
};

export default BookIndexRow;
