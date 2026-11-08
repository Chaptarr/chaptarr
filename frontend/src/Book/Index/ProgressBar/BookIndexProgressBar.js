import PropTypes from 'prop-types';
import React from 'react';
import ProgressBar from 'Components/ProgressBar';
import { kinds, sizes } from 'Helpers/Props';
import isBefore from 'Utilities/Date/isBefore';
import translate from 'Utilities/String/translate';
import styles from './BookIndexProgressBar.css';

function BookIndexProgressBar(props) {
  const {
    monitored,
    bookFileCount,
    releaseDate,
    posterWidth,
    detailedProgressBar
  } = props;

  const hasBookFile = bookFileCount > 0;
  let kind = kinds.WARNING;

  if (hasBookFile) {
    kind = kinds.SUCCESS;
  } else if (monitored) {
    kind = isBefore(releaseDate) ? kinds.DANGER : kinds.PRIMARY;
  }

  const bookCount = hasBookFile ? 1 : 0;
  const totalBookCount = 1;
  const text = `${bookCount} / ${totalBookCount}`;

  return (
    <ProgressBar
      className={styles.progressBar}
      containerClassName={styles.progress}
      progress={100}
      kind={kind}
      size={detailedProgressBar ? sizes.MEDIUM : sizes.SMALL}
      showText={detailedProgressBar}
      text={text}
      title={translate('BookProgressBarText', {
        bookCount: bookFileCount ? bookCount : 0,
        bookFileCount,
        totalBookCount
      })}
      width={posterWidth}
    />
  );
}

BookIndexProgressBar.propTypes = {
  monitored: PropTypes.bool.isRequired,
  bookFileCount: PropTypes.number.isRequired,
  releaseDate: PropTypes.string,
  posterWidth: PropTypes.number.isRequired,
  detailedProgressBar: PropTypes.bool.isRequired
};

export default BookIndexProgressBar;
