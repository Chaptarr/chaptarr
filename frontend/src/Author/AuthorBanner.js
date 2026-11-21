import PropTypes from 'prop-types';
import React from 'react';
import getPathWithUrlBase from 'Utilities/getPathWithUrlBase';
import AuthorImage from './AuthorImage';

const bannerPlaceholder = '/Content/Images/chaptarr-logo.svg';

function AuthorBanner(props) {
  return (
    <AuthorImage
      {...props}
      coverType="banner"
      placeholder={getPathWithUrlBase(bannerPlaceholder)}
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
