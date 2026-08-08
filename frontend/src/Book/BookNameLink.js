import PropTypes from 'prop-types';
import React from 'react';
import Link from 'Components/Link/Link';

function BookNameLink({ titleSlug, localBookId, title }) {
  const link = localBookId ? `/book/${localBookId}` : `/book/${titleSlug}`;

  return (
    <Link to={link}>
      {title}
    </Link>
  );
}

BookNameLink.propTypes = {
  titleSlug: PropTypes.string,
  localBookId: PropTypes.string,
  title: PropTypes.string.isRequired
};

export default BookNameLink;
