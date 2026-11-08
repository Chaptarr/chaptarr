import PropTypes from 'prop-types';

function MediaManagementEmptyStub({ onConfirmResetApiKey }) {
  if (onConfirmResetApiKey) {
    window.confirmGeneralReset = onConfirmResetApiKey;
  }
  return null;
}

MediaManagementEmptyStub.propTypes = {
  onConfirmResetApiKey: PropTypes.func
};

export default MediaManagementEmptyStub;
