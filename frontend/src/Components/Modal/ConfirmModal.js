import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function ConfirmModal(props) {
  const {
    isOpen,
    kind,
    size,
    title,
    message,
    confirmLabel,
    cancelLabel,
    hideCancelButton,
    isSpinning,
    onConfirm,
    onCancel
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      size={size}
      onModalClose={onCancel}
      onEnterPress={isSpinning ? undefined : onConfirm}
    >
      <ModalContent onModalClose={onCancel}>
        <ModalHeader>{title}</ModalHeader>

        <ModalBody>
          {message}
        </ModalBody>

        <ModalFooter>
          {
            !hideCancelButton &&
              <Button
                kind={kinds.DEFAULT}
                onPress={onCancel}
              >
                {cancelLabel || translate('Cancel')}
              </Button>
          }

          <SpinnerButton
            autoFocus={true}
            kind={kind}
            isSpinning={isSpinning}
            onPress={onConfirm}
          >
            {confirmLabel || translate('Ok')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

ConfirmModal.propTypes = {
  className: PropTypes.string,
  isOpen: PropTypes.bool.isRequired,
  kind: PropTypes.oneOf(kinds.all),
  size: PropTypes.oneOf(sizes.all),
  title: PropTypes.string.isRequired,
  message: PropTypes.oneOfType([PropTypes.string, PropTypes.node]).isRequired,
  confirmLabel: PropTypes.string,
  cancelLabel: PropTypes.string,
  hideCancelButton: PropTypes.bool,
  isSpinning: PropTypes.bool.isRequired,
  onConfirm: PropTypes.func.isRequired,
  onCancel: PropTypes.func.isRequired
};

ConfirmModal.defaultProps = {
  kind: kinds.PRIMARY,
  size: sizes.MEDIUM,
  isSpinning: false
};

export default ConfirmModal;
