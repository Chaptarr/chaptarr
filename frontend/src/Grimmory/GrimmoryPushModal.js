import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import GrimmoryPushModalContent from './GrimmoryPushModalContent';

function GrimmoryPushModal(props) {
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
          <GrimmoryPushModalContent
            {...otherProps}
            onModalClose={onModalClose}
          />
      }
    </Modal>
  );
}

GrimmoryPushModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default GrimmoryPushModal;
