import PropTypes from 'prop-types';
import React from 'react';
import Link from 'Components/Link/Link';

function AuthorNameLink({ authorId, authorName, ...otherProps }) {
  const link = `/author/${authorId}`;

  return (
    <Link to={link} {...otherProps}>
      {authorName}
    </Link>
  );
}

AuthorNameLink.propTypes = {
  authorId: PropTypes.oneOfType([PropTypes.number, PropTypes.string]).isRequired,
  authorName: PropTypes.string.isRequired
};

export default AuthorNameLink;
