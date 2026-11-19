import PropTypes from 'prop-types';
import React from 'react';
import AuthorInteractiveSearchTable from './AuthorInteractiveSearchTable';
import InteractiveSearchConnector from './InteractiveSearchConnector';

function InteractiveSearchTable(props) {
  const {
    type,
    selectedMediaType,
    searchCount,
    ...otherProps
  } = props;

  if (type === 'author') {
    return (
      <AuthorInteractiveSearchTable
        {...otherProps}
        selectedMediaType={selectedMediaType}
        searchCount={searchCount}
      />
    );
  }

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
  searchCount: PropTypes.number,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook'])
};

export default InteractiveSearchTable;
