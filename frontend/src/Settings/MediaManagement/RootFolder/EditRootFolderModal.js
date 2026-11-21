import PropTypes from 'prop-types';
import React, { useRef } from 'react';
import Modal from 'Components/Modal/Modal';
import EditRootFolderModalContentConnector from './EditRootFolderModalContentConnector';

function EditRootFolderModal({ isOpen, onModalClose, ...otherProps }) {
  const connectorRef = useRef(null);
  return (
    <Modal
      isOpen={isOpen}
      onModalClose={() => (connectorRef.current ? connectorRef.current.onModalClose() : onModalClose())}
    >
      <EditRootFolderModalContentConnector
        {...otherProps}
        ref={connectorRef}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

EditRootFolderModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSaveSuccess: PropTypes.func,
  folderType: PropTypes.number
};

export default EditRootFolderModal;
