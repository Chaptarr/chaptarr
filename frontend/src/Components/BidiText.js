import PropTypes from 'prop-types';
import React from 'react';

function BidiText(props) {
  const { children, ...otherProps } = props;

  return (
    <bdi dir="auto" {...otherProps}>
      {children}
    </bdi>
  );
}

BidiText.propTypes = {
  children: PropTypes.node.isRequired
};

export default BidiText;
