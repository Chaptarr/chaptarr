import classNames from 'classnames';
import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import styles from './BookMediaTypeBadge.css';

function BookMediaTypeBadge(props) {
  const { mediaType, className } = props;
  const isEbook = mediaType === 'ebook';

  return (
    <span className={classNames(styles.badge, className)}>
      {translate(isEbook ? 'EbookLabel' : 'AudiobookLabel')}
    </span>
  );
}

BookMediaTypeBadge.propTypes = {
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  className: PropTypes.string
};

BookMediaTypeBadge.defaultProps = {
  mediaType: 'audiobook'
};

export default BookMediaTypeBadge;
