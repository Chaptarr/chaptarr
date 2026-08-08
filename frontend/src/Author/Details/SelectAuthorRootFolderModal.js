import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import SelectAuthorRootFolderModalContentConnector from './SelectAuthorRootFolderModalContentConnector';

function SelectAuthorRootFolderModal({ isOpen, onModalClose, ...otherProps }) {
  return (
    <Modal
      isOpen={isOpen}
      onModalClose={onModalClose}
    >
      <SelectAuthorRootFolderModalContentConnector
        {...otherProps}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

SelectAuthorRootFolderModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  authorId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SelectAuthorRootFolderModal;

