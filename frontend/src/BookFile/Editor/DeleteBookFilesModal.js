import PropTypes from 'prop-types';
import React, { useState } from 'react';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import BookFileDeletionPreview, { deletionSelection } from './BookFileDeletionPreview';

function DeleteBookFilesModal({ isOpen, fileIds, onConfirm, onCancel }) {
  const [readySelection, setReadySelection] = useState(null);
  const ready = isOpen && readySelection === deletionSelection(fileIds);

  return (
    <Modal isOpen={isOpen} onModalClose={onCancel}>
      <ModalContent onModalClose={onCancel}>
        <ModalHeader>{translate(fileIds.length === 1 ? 'DeleteBookFile' : 'DeleteSelectedBookFiles')}</ModalHeader>
        <ModalBody>
          <p>{translate('DeleteSelectedBookFilesMessageText')}</p>
          {isOpen &&
            <BookFileDeletionPreview key={deletionSelection(fileIds)} fileIds={fileIds}
              onReadyChange={setReadySelection}
            />
          }
        </ModalBody>
        <ModalFooter>
          <Button onPress={onCancel}>{translate('Cancel')}</Button>
          <Button kind={kinds.DANGER} isDisabled={!ready}
            onPress={ready ? onConfirm : undefined}
          >
            {translate('Delete')}
          </Button>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

DeleteBookFilesModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  fileIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  onConfirm: PropTypes.func.isRequired,
  onCancel: PropTypes.func.isRequired
};

export default DeleteBookFilesModal;
