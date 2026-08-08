import PropTypes from 'prop-types';
import React from 'react';
import AuthorPoster from 'Author/AuthorPoster';
import BookFormatActions from 'Components/Book/BookFormatActions';
import styles from './BookSearchResult.css';

function BookSearchResult(props) {
  const {
    title,
    authorName,
    images,
    monitored,
    localAudiobookBooks,
    localEbookBooks
  } = props;

  return (
    <div className={styles.result}>
      <AuthorPoster
        className={styles.poster}
        images={images}
        coverType={'cover'}
        size={250}
        lazy={false}
        overflow={true}
      />

      <div className={styles.titles}>
        <div className={monitored ? styles.title : styles.titleUnmonitored}>
          <span className={styles.titleText}>{title}</span>
          <BookFormatActions
            title={title}
            localAudiobookBooks={localAudiobookBooks}
            localEbookBooks={localEbookBooks}
            canAdd={false}
            size={16}
            showAddActions={false}
          />
        </div>

        {
          !!authorName &&
            <div className={styles.subtitle}>
              {authorName}
            </div>
        }
      </div>
    </div>
  );
}

BookSearchResult.propTypes = {
  title: PropTypes.string.isRequired,
  authorName: PropTypes.string,
  monitored: PropTypes.bool.isRequired,
  localAudiobookBooks: PropTypes.arrayOf(PropTypes.object),
  localEbookBooks: PropTypes.arrayOf(PropTypes.object),
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  match: PropTypes.object
};

export default BookSearchResult;
