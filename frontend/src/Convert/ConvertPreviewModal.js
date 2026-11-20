import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import ConvertPreviewModalContentConnector from './ConvertPreviewModalContentConnector';

function ConvertPreviewModal(props) {
  const {
    isOpen,
    onModalClose,
    ...otherProps
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      onModalClose={onModalClose}
    >
      {
        isOpen &&
          <ConvertPreviewModalContentConnector
            {...otherProps}
            onModalClose={onModalClose}
          />
      }
    </Modal>
  );
}

ConvertPreviewModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ConvertPreviewModal;
