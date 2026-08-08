import PropTypes from 'prop-types';
import React from 'react';
import AuthorImage from './AuthorImage';

const bannerPlaceholder = '/Content/Images/chaptarr-logo.svg';

function AuthorBanner(props) {
  return (
    <AuthorImage
      {...props}
      coverType="banner"
      placeholder={bannerPlaceholder}
    />
  );
}

AuthorBanner.propTypes = {
  size: PropTypes.number.isRequired
};

AuthorBanner.defaultProps = {
  size: 70
};

export default AuthorBanner;
