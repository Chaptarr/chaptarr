import translateServerMessage from 'Utilities/String/translateServerMessage';

function getErrorMessage(xhr, fallbackErrorMessage) {
  const response = xhr && xhr.responseJSON;
  const message = typeof response === 'string' ? response :
    response && (response.message || response.errorMessage || response.ErrorMessage || response.error);

  return translateServerMessage(typeof message === 'string' && message ? message : fallbackErrorMessage);
}

export default getErrorMessage;
