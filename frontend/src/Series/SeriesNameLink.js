import PropTypes from 'prop-types';
import React from 'react';
import Link from 'Components/Link/Link';

function SeriesNameLink({ localSeriesId, seriesName, ...otherProps }) {
  const link = `/series/${localSeriesId}`;

  return (
    <Link to={link} {...otherProps}>
      {seriesName}
    </Link>
  );
}

SeriesNameLink.propTypes = {
  localSeriesId: PropTypes.string.isRequired,
  seriesName: PropTypes.string.isRequired
};

export default SeriesNameLink;