import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import EncryptedBackupModalContent from './EncryptedBackupModalContent';

function EncryptedBackupModal(props) {
  const { isOpen, onModalClose } = props;

  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <EncryptedBackupModalContent onModalClose={onModalClose} />
    </Modal>
  );
}

EncryptedBackupModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default EncryptedBackupModal;
