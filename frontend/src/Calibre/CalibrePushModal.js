import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import CalibrePushModalContent from './CalibrePushModalContent';

function CalibrePushModal(props) {
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
          <CalibrePushModalContent
            {...otherProps}
            onModalClose={onModalClose}
          />
      }
    </Modal>
  );
}

CalibrePushModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default CalibrePushModal;
