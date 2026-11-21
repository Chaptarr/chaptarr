import PropTypes from 'prop-types';
import React from 'react';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function PendingChangesModal(props) {
  const {
    isOpen,
    onConfirm,
    onCancel
  } = props;

  return (
    <ConfirmModal
      isOpen={isOpen}
      kind={kinds.DANGER}
      size={sizes.LARGE}
      title={translate('UnsavedChanges')}
      message={translate('PendingChangesMessage')}
      confirmLabel={translate('PendingChangesDiscardLeave')}
      cancelLabel={translate('PendingChangesStayReview')}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}

PendingChangesModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onConfirm: PropTypes.func.isRequired,
  onCancel: PropTypes.func.isRequired
};

export default PendingChangesModal;
