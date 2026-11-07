import PropTypes from 'prop-types';
import React from 'react';
import BookshelfBook from './BookshelfBook';
import styles from './BookshelfWorkGroup.css';

function getFormatLabel(mediaType) {
  return mediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel';
}

function BookshelfWorkGroup(props) {
  const { group, onBookMonitoredPress } = props;

  return (
    <div className={styles.workGroup}>
      <div className={styles.workTitle}>{group.title}</div>
      <div className={styles.formats}>
        {
          group.books.map((book) => (
            <BookshelfBook
              key={book.id}
              {...book}
              formatLabelKey={getFormatLabel(book.mediaType)}
              onBookMonitoredPress={onBookMonitoredPress}
            />
          ))
        }
      </div>
    </div>
  );
}

BookshelfWorkGroup.propTypes = {
  group: PropTypes.shape({
    title: PropTypes.string,
    books: PropTypes.arrayOf(PropTypes.object).isRequired
  }).isRequired,
  onBookMonitoredPress: PropTypes.func.isRequired
};

export default BookshelfWorkGroup;
