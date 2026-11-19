import PropTypes from 'prop-types';
import React from 'react';

// The page wrapper, store and requests are real; the loaded detail body is outside this lifecycle check.
function BookRouteDetailWitness({ bookId }) {
  return <div data-loaded-book={bookId}>{bookId}</div>;
}

BookRouteDetailWitness.propTypes = { bookId: PropTypes.number.isRequired };

export default BookRouteDetailWitness;
