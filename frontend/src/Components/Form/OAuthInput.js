import PropTypes from 'prop-types';
import React from 'react';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import { kinds } from 'Helpers/Props';

function getOAuthErrorText(error) {
  if (!error) {
    return null;
  }

  const responseJSON = error.responseJSON;

  if (Array.isArray(responseJSON)) {
    const messages = responseJSON
      .map((e) => e?.errorMessage)
      .filter(Boolean);

    return messages.length ? messages.join(' ') : (error.responseText || null);
  }

  if (responseJSON && typeof responseJSON === 'object') {
    return responseJSON.message || error.responseText || null;
  }

  return error.responseText || null;
}

function OAuthInput(props) {
  const {
    label,
    authorizing,
    error,
    providerData,
    onPress
  } = props;

  const errorText = getOAuthErrorText(error);

  const implementationName = providerData && typeof providerData.implementationName === 'string'
    ? providerData.implementationName
    : '';

  const isPlex = implementationName.toLowerCase().includes('plex');
  const kind = isPlex ? kinds.WARNING : kinds.PRIMARY;

  return (
    <div>
      <SpinnerErrorButton
        kind={kind}
        isSpinning={authorizing}
        error={error}
        onPress={onPress}
      >
        {label}
      </SpinnerErrorButton>

      {
        errorText ?
          <FormInputHelpText
            text={errorText}
            isError={true}
          /> :
          null
      }
    </div>
  );
}

OAuthInput.propTypes = {
  label: PropTypes.string.isRequired,
  authorizing: PropTypes.bool.isRequired,
  error: PropTypes.object,
  providerData: PropTypes.object,
  onPress: PropTypes.func.isRequired
};

OAuthInput.defaultProps = {
  label: 'Start OAuth'
};

export default OAuthInput;
