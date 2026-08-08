import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookSearchCellConnector from 'Book/BookSearchCellConnector';
import BookTitleLink from 'Book/BookTitleLink';
import IndexerFlags from 'Book/IndexerFlags';
import NarratorCell from 'Components/Book/NarratorCell';
import Icon from 'Components/Icon';
import MonitorToggleButton from 'Components/MonitorToggleButton';
import StarRating from 'Components/StarRating';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import formatReadableDuration from 'Utilities/Date/formatReadableDuration';
import translate from 'Utilities/String/translate';
import BookStatus from './BookStatus';
import styles from './BookRow.css';

class BookRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isDetailsModalOpen: false,
      isEditBookModalOpen: false
    };
  }

  //
  // Listeners

  onManualSearchPress = () => {
    this.setState({ isDetailsModalOpen: true });
  };

  onDetailsModalClose = () => {
    this.setState({ isDetailsModalOpen: false });
  };

  onEditBookPress = () => {
    this.setState({ isEditBookModalOpen: true });
  };

  onEditBookModalClose = () => {
    this.setState({ isEditBookModalOpen: false });
  };

  onMonitorBookPress = (monitored, options) => {
    this.props.onMonitorBookPress(this.props.id, monitored, options);
  };

  //
  // Render

  render() {
    const {
      id,
      authorId,
      audiobookMonitored,
      ebookMonitored,
      anyEditionOk,
      hasFiles,
      mediaType,
      selectedMediaType,
      releaseDate,
      title,
      seriesTitle,
      authorName,
      narrator,
      narratorNames,
      availableNarrators,
      position,
      pageCount,
      duration,
      ratings,
      isSaving,
      bookFiles,
      grabbed,
      indexerFlags,
      queueItem,
      isEditorActive,
      isSelected,
      onSelectedChange,
      columns,
      editions
    } = this.props;

    const bookFile = bookFiles[0];
    const isAvailable = !releaseDate || Date.parse(releaseDate) < new Date();
    const hasNarrator = narrator && (typeof narrator !== 'string' || narrator.trim().length > 0);
    const hasFilesForDisplay = (typeof hasFiles === 'boolean') ? hasFiles : bookFiles.length > 0;
    const hasPinnedEdition = (anyEditionOk === false) || (Array.isArray(editions) && editions.some((e) => e?.manualAdd === true));
    const canDisplayAudioMetadata = hasFilesForDisplay || hasPinnedEdition;

    return (
      <TableRow>
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
                <TableSelectCell
                  key={name}
                  id={id}
                  isSelected={isSelected}
                  isDisabled={false}
                  onSelectedChange={onSelectedChange}
                />
              );
            }

            if (name === 'monitored') {
              // Use media-type specific monitoring field
              const isMonitored = selectedMediaType === 'audiobook' ? audiobookMonitored : ebookMonitored;

              return (
                <TableRowCell
                  key={name}
                  className={styles.monitored}
                >
                  <MonitorToggleButton
                    monitored={isMonitored}
                    isDisabled={false}
                    isSaving={isSaving}
                    isBinary={true}
                    onPress={this.onMonitorBookPress}
                  />
                </TableRowCell>
              );
            }

            if (name === 'title') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.title}
                >
                  <BookTitleLink
                    bookId={id}
                    title={title}
                  />
                </TableRowCell>
              );
            }

            if (name === 'narrator') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.narrator}
                >
                  {(selectedMediaType === 'audiobook' || bookFiles.length > 0 || hasNarrator) ? (
                    <NarratorCell
                      bookId={id}
                      narrator={narrator}
                      narratorNames={narratorNames}
                      availableNarrators={availableNarrators}
                      hasFiles={hasFilesForDisplay}
                      hasPinnedEdition={hasPinnedEdition}
                      authorName={authorName}
                      bookTitle={title}
                    />
                  ) : null}
                </TableRowCell>
              );
            }

            if (name === 'series') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.title}
                >
                  {seriesTitle || ''}
                </TableRowCell>
              );
            }

            if (name === 'position') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.position}
                >
                  {position || ''}
                </TableRowCell>
              );
            }

            if (name === 'rating') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.rating}
                >
                  {
                    <StarRating
                      rating={ratings.value}
                      votes={ratings.votes}
                    />
                  }
                </TableRowCell>
              );
            }

            if (name === 'releaseDate') {
              return (
                <RelativeDateCellConnector
                  className={styles.releaseDate}
                  key={name}
                  date={releaseDate}
                  timeForToday={false}
                />
              );
            }

            if (name === 'pageCount') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.pageCount}
                >
                  {pageCount || ''}
                </TableRowCell>
              );
            }

            if (name === 'duration') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.duration}
                >
                  {(selectedMediaType === 'audiobook' && canDisplayAudioMetadata && duration) ? formatReadableDuration(duration) : ''}
                </TableRowCell>
              );
            }

            if (name === 'indexerFlags') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.indexerFlags}
                >
                  {indexerFlags ? (
                    <Popover
                      anchor={<Icon name={icons.FLAG} kind={kinds.PRIMARY} />}
                      title={translate('IndexerFlags')}
                      body={<IndexerFlags indexerFlags={indexerFlags} />}
                      position={tooltipPositions.LEFT}
                    />
                  ) : null}
                </TableRowCell>
              );
            }

            if (name === 'status') {
              // Use media-type specific monitoring field for status display
              const isMonitored = selectedMediaType === 'audiobook' ? audiobookMonitored : ebookMonitored;

              return (
                <TableRowCell
                  key={name}
                  className={styles.status}
                >
                  <BookStatus
                    grabbed={grabbed}
                    isAvailable={isAvailable}
                    monitored={isMonitored}
                    bookFile={bookFile}
                    queueItem={queueItem}
                  />
                </TableRowCell>
              );
            }

            if (name === 'actions') {
              return (
                <BookSearchCellConnector
                  key={name}
                  bookId={id}
                  authorId={authorId}
                  bookTitle={title}
                  authorName={authorName}
                  selectedMediaType={mediaType || selectedMediaType}
                />
              );
            }
            return null;
          })
        }
      </TableRow>
    );
  }
}

BookRow.propTypes = {
  id: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  audiobookMonitored: PropTypes.bool,
  ebookMonitored: PropTypes.bool,
  anyEditionOk: PropTypes.bool,
  hasFiles: PropTypes.bool,
  mediaType: PropTypes.string,
  selectedMediaType: PropTypes.string,
  releaseDate: PropTypes.string,
  title: PropTypes.string.isRequired,
  seriesTitle: PropTypes.string,
  authorName: PropTypes.string.isRequired,
  narrator: PropTypes.string,
  narratorNames: PropTypes.arrayOf(PropTypes.string),
  availableNarrators: PropTypes.arrayOf(PropTypes.string),
  position: PropTypes.string,
  pageCount: PropTypes.number,
  duration: PropTypes.string,
  ratings: PropTypes.oneOfType([
    PropTypes.object,
    PropTypes.number
  ]).isRequired,
  indexerFlags: PropTypes.number.isRequired,
  isSaving: PropTypes.bool,
  bookFiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  grabbed: PropTypes.bool,
  editions: PropTypes.arrayOf(PropTypes.object),
  isEditorActive: PropTypes.bool,
  queueItem: PropTypes.object,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  onMonitorBookPress: PropTypes.func.isRequired
};

BookRow.defaultProps = {
  indexerFlags: 0
};

export default BookRow;
