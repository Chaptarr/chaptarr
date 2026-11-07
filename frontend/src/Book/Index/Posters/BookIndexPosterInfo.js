import PropTypes from 'prop-types';
import React from 'react';
import getBookAuthorPath from 'Utilities/Book/getBookAuthorPath';
import getRelativeDate from 'Utilities/Date/getRelativeDate';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './BookIndexPosterInfo.css';

function BookIndexPosterInfo(props) {
  const {
    qualityProfile,
    showQualityProfile,
    added,
    releaseDate,
    mediaType,
    author,
    bookFileCount,
    sizeOnDisk,
    sortKey,
    showRelativeDates,
    shortDateFormat,
    timeFormat
  } = props;

  if (sortKey === 'qualityProfileId' && !showQualityProfile) {
    return (
      <div className={styles.info}>
        {qualityProfile.name}
      </div>
    );
  }

  if (sortKey === 'added' && added) {
    const addedDate = getRelativeDate(
      added,
      shortDateFormat,
      showRelativeDates,
      {
        timeFormat,
        timeForToday: false
      }
    );

    return (
      <div className={styles.info}>
        {`${translate('Added')} ${addedDate}`}
      </div>
    );
  }

  if (sortKey === 'releaseDate' && added) {
    const date = getRelativeDate(
      releaseDate,
      shortDateFormat,
      showRelativeDates,
      {
        timeFormat,
        timeForToday: false
      }
    );

    return (
      <div className={styles.info}>
        {`${translate('Released')} ${date}`}
      </div>
    );
  }

  if (sortKey === 'bookFileCount') {
    let files = translate('FileCountMessage');

    if (bookFileCount === 0) {
      files = translate('NoFiles');
    } else if (bookFileCount > 1) {
      files = translate('FilesCountMessage', { count: bookFileCount });
    }

    return (
      <div className={styles.info}>
        {files}
      </div>
    );
  }

  if (sortKey === 'path') {
    return (
      <div className={styles.info}>
        {getBookAuthorPath({ author, mediaType })}
      </div>
    );
  }

  if (sortKey === 'sizeOnDisk') {
    return (
      <div className={styles.info}>
        {formatBytes(sizeOnDisk)}
      </div>
    );
  }

  return null;
}

BookIndexPosterInfo.propTypes = {
  qualityProfile: PropTypes.object.isRequired,
  showQualityProfile: PropTypes.bool.isRequired,
  author: PropTypes.object.isRequired,
  mediaType: PropTypes.string.isRequired,
  added: PropTypes.string,
  releaseDate: PropTypes.string,
  bookFileCount: PropTypes.number.isRequired,
  sizeOnDisk: PropTypes.number,
  sortKey: PropTypes.string.isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired
};

export default BookIndexPosterInfo;
