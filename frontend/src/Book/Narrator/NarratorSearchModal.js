import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import NarratorSearchModalContentConnector from './NarratorSearchModalContentConnector';

function NarratorSearchModal({ isOpen, onModalClose, ...otherProps }) {
  return (
    <Modal
      isOpen={isOpen}
      onModalClose={onModalClose}
    >
      <NarratorSearchModalContentConnector
        {...otherProps}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

NarratorSearchModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default NarratorSearchModal;
