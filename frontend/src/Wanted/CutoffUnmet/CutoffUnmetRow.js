import PropTypes from 'prop-types';
import React from 'react';
import AuthorNameLink from 'Author/AuthorNameLink';
import bookEntities from 'Book/bookEntities';
import BookSearchCellConnector from 'Book/BookSearchCellConnector';
import BookTitleLink from 'Book/BookTitleLink';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import styles from './CutoffUnmetRow.css';

const mediaTypeIcons = {
  audiobook: '🎧',
  ebook: '📖'
};

function CutoffUnmetRow(props) {
  const {
    id,
    author,
    releaseDate,
    titleSlug,
    title,
    mediaType,
    narrator,
    lastSearchTime,
    disambiguation,
    isSelected,
    columns,
    onSelectedChange
  } = props;

  if (!author) {
    return null;
  }

  return (
    <TableRow>
      <TableSelectCell
        id={id}
        isSelected={isSelected}
        onSelectedChange={onSelectedChange}
      />

      {
        columns.map((column) => {
          const {
            name,
            isVisible
          } = column;

          if (!isVisible) {
            return null;
          }

          if (name === 'authors.sortName') {
            return (
              <TableRowCell
                key={name}
                className={styles.author}
              >
                <AuthorNameLink
                  titleSlug={author.titleSlug}
                  authorId={author.id}
                  authorName={author.authorName}
                />
              </TableRowCell>
            );
          }

          if (name === 'books.title') {
            return (
              <TableRowCell
                key={name}
                className={styles.bookTitle}
              >
                <BookTitleLink
                  titleSlug={titleSlug}
                  bookId={id}
                  title={title}
                  disambiguation={disambiguation}
                />
              </TableRowCell>
            );
          }

          if (name === 'mediaType') {
            const normalizedMediaType = (mediaType || '').toString().toLowerCase();
            const icon = mediaTypeIcons[normalizedMediaType] || '';

            return (
              <TableRowCell
                key={name}
                className={styles.mediaType}
              >
                {icon}
              </TableRowCell>
            );
          }

          if (name === 'narrator') {
            return (
              <TableRowCell
                key={name}
                className={styles.narrator}
              >
                {narrator || '-'}
              </TableRowCell>
            );
          }

          if (name === 'books.lastSearchTime') {
            return (
              <RelativeDateCellConnector
                key={name}
                date={lastSearchTime}
              />
            );
          }

          if (name === 'releaseDate') {
            return (
              <RelativeDateCellConnector
                key={name}
                date={releaseDate}
                timeForToday={false}
              />
            );
          }

          if (name === 'actions') {
            return (
              <BookSearchCellConnector
                key={name}
                bookId={id}
                authorId={author.id}
                bookTitle={title}
                authorName={author.authorName}
                selectedMediaType={mediaType}
                bookEntity={bookEntities.WANTED_CUTOFF_UNMET}
                showOpenAuthorButton={true}
              />
            );
          }

          return null;
        })
      }
    </TableRow>
  );
}

CutoffUnmetRow.propTypes = {
  id: PropTypes.number.isRequired,
  bookFileId: PropTypes.number,
  author: PropTypes.object.isRequired,
  releaseDate: PropTypes.string.isRequired,
  titleSlug: PropTypes.string.isRequired,
  localBookId: PropTypes.string,
  title: PropTypes.string.isRequired,
  mediaType: PropTypes.string,
  narrator: PropTypes.string,
  lastSearchTime: PropTypes.string,
  disambiguation: PropTypes.string,
  isSelected: PropTypes.bool,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  onSelectedChange: PropTypes.func.isRequired
};

export default CutoffUnmetRow;
