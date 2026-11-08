function getErrorMessage(xhr, fallbackErrorMessage) {
  if (Array.isArray(xhr?.responseJSON)) {
    return xhr.responseJSON.map((error) => error.errorMessage || error.message).filter(Boolean).join('\n') || fallbackErrorMessage;
  }

  if (!xhr || !xhr.responseJSON || !xhr.responseJSON.message) {
    return fallbackErrorMessage;
  }

  const message = xhr.responseJSON.message;

  return message || fallbackErrorMessage;
}

export default getErrorMessage;
