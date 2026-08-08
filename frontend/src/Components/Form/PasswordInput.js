import PropTypes from 'prop-types';
import React from 'react';
import TextInput from './TextInput';

// Prevent a user from copying (or cutting) the password from the input
function onCopy(e) {
  e.preventDefault();
  e.nativeEvent.stopImmediatePropagation();
}

function PasswordInput(props) {
  const {
    privacy,
    ...otherProps
  } = props;

  const preventCopy = privacy === 'password';

  return (
    <TextInput
      {...otherProps}
      type="password"
      onCopy={preventCopy ? onCopy : undefined}
    />
  );
}

PasswordInput.propTypes = {
  ...TextInput.propTypes,
  privacy: PropTypes.string
};

export default PasswordInput;
