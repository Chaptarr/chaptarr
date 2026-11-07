import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorNameLink from 'Author/AuthorNameLink';
import { getAuthorStatusDetails } from 'Author/AuthorStatus';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import VirtualTableSelectCell from 'Components/Table/Cells/VirtualTableSelectCell';
import translate from 'Utilities/String/translate';
import BookshelfBook from './BookshelfBook';
import BookshelfWorkGroup from './BookshelfWorkGroup';
import { getBookshelfWorkGroups } from './workGroups.cjs';
import styles from './BookshelfRow.css';

class BookshelfRow extends Component {

  //
  // Render

  render() {
    const {
      authorId,
      status,
      titleSlug,
      authorName,
      books,
      selectedMediaType,
      isBooksLoaded,
      hasBooksError,
      onRetryBooksPress,
      isSelected,
      onSelectedChange,
      onBookMonitoredPress
    } = this.props;

    const statusDetails = getAuthorStatusDetails(status);

    return (
      <>
        <VirtualTableSelectCell
          className={styles.selectCell}
          id={authorId}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
          isDisabled={false}
        />

        <VirtualTableRowCell className={styles.status}>
          <Icon
            className={styles.statusIcon}
            name={statusDetails.icon}
            title={statusDetails.title}
          />
        </VirtualTableRowCell>

        <VirtualTableRowCell className={styles.title}>
          <AuthorNameLink
            titleSlug={titleSlug}
            authorId={authorId}
            authorName={authorName}
          />
        </VirtualTableRowCell>

        <VirtualTableRowCell className={styles.books}>
          {
            hasBooksError &&
              <Button onPress={onRetryBooksPress}>
                {translate('Retry')}
              </Button>
          }

          {
            !isBooksLoaded && !hasBooksError && !books.length &&
              <LoadingIndicator
                className={styles.booksLoading}
                size={20}
              />
          }

          {
            selectedMediaType === 'both' ?
              getBookshelfWorkGroups(books).map((group) => {
                if (group.books.length === 1) {
                  const book = group.books[0];

                  return (
                    <BookshelfBook
                      key={group.key}
                      {...book}
                      formatLabelKey={book.mediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel'}
                      onBookMonitoredPress={onBookMonitoredPress}
                    />
                  );
                }

                return (
                  <BookshelfWorkGroup
                    key={group.key}
                    group={group}
                    onBookMonitoredPress={onBookMonitoredPress}
                  />
                );
              }) :
              books.map((book) => (
                <BookshelfBook
                  key={book.id}
                  {...book}
                  onBookMonitoredPress={onBookMonitoredPress}
                />
              ))
          }
        </VirtualTableRowCell>
      </>
    );
  }
}

BookshelfRow.propTypes = {
  authorId: PropTypes.number.isRequired,
  status: PropTypes.string.isRequired,
  titleSlug: PropTypes.string.isRequired,
  authorName: PropTypes.string.isRequired,
  books: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  isBooksLoaded: PropTypes.bool.isRequired,
  hasBooksError: PropTypes.bool.isRequired,
  onRetryBooksPress: PropTypes.func.isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired,
  onBookMonitoredPress: PropTypes.func.isRequired
};

export default BookshelfRow;
