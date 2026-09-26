import React from 'react';
import ReactDOM from 'react-dom';
import ConfirmModal from 'Components/Modal/ConfirmModal';

import 'Styles/scaffolding.css';

window.confirmCalls = 0;
window.cancelCalls = 0;

const message = (
  <div data-testid="modal-content" tabIndex={0}>
    {'Confirm this action?'}
  </div>
);

ReactDOM.render(
  <ConfirmModal
    isOpen={true}
    title="Confirm action"
    message={message}
    confirmLabel="Confirm"
    cancelLabel="Cancel"
    onConfirm={() => {
      window.confirmCalls += 1;
    }}
    onCancel={() => {
      window.cancelCalls += 1;
    }}
  />,
  document.getElementById('root')
);

window.fixtureReady = true;
