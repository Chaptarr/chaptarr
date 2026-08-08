import PropTypes from 'prop-types';
import React from 'react';
import InteractiveSearchConnector from './InteractiveSearchConnector';

function InteractiveSearchTable(props) {
  const {
    type,
    selectedMediaType,
    ...otherProps
  } = props;

  return (
    <InteractiveSearchConnector
      searchPayload={{
        ...otherProps,
        initialMediaType: selectedMediaType
      }}
      type={type}
    />
  );
}

InteractiveSearchTable.propTypes = {
  type: PropTypes.string.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook'])
};

export default InteractiveSearchTable;
