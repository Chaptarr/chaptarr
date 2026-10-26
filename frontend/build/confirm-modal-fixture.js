import { createMemoryHistory } from 'history';
import React, { useEffect, useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Route, Router, Switch } from 'react-router-dom';
import { createStore } from 'redux';
import TextInput from 'Components/Form/TextInput';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import importStyles from 'InteractiveImport/Interactive/InteractiveImportModalContent.css';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const scenario = new URLSearchParams(window.location.search).get('scenario') || 'confirm';
const history = createMemoryHistory({ initialEntries: ['/settings/general'] });
const store = createStore((state = {
  settings: { advancedSettings: false },
  app: { dimensions: { width: window.innerWidth, height: window.innerHeight, isSmallScreen: window.innerWidth < 768 } }
}) => state);

window.calls = { confirm: 0, cancel: 0, link: 0, nested: 0, unmount: 0 };
window.fixtureHistory = history;

function SettingsPage() {
  const [value, setValue] = useState('Original');

  useEffect(() => () => {
    window.calls.unmount += 1;
  }, []);

  return (
    <div>
      <SettingsToolbarConnector
        hasPendingChanges={value !== 'Original'}
        isSaving={false}
        onSavePress={() => {
          window.calls.confirm += 1;
        }}
      />
      <TextInput
        name="setting"
        value={value}
        onChange={({ value: next }) => setValue(next)}
      />
      <Link to="/other">{'Leave settings'}</Link>
    </div>
  );
}

function ConfirmationFixture() {
  const [isOpen, setIsOpen] = useState(scenario !== 'reopen');
  const [isSpinning, setIsSpinning] = useState(false);
  const [nested, setNested] = useState(false);
  const [value, setValue] = useState('Text');

  const onCancel = () => {
    window.calls.cancel += 1;
    setIsOpen(false);
  };

  return (
    <div>
      <Button onPress={() => setIsOpen(true)}>{'Open dialog'}</Button>
      <ConfirmModal
        isOpen={isOpen}
        isSpinning={isSpinning}
        title="Confirm action"
        message={(
          <div data-testid="modal-content" tabIndex={0}>
            {'Confirm this action?'}
            <Link
              to="https://example.test/info"
              onPress={(event) => {
                event.preventDefault();
                window.calls.link += 1;
                setIsOpen(false);
              }}
            >
              {'Read details'}
            </Link>
            {
              scenario === 'inputs' &&
                <div>
                  <TextInput name="text" value={value}
                    onChange={({ value: next }) => setValue(next)}
                  />
                  <textarea aria-label="Notes" defaultValue="First line" />
                  <select aria-label="Choice" defaultValue="one"><option value="one">{'One'}</option></select>
                </div>
            }
            {
              scenario === 'nested' &&
                <Button onPress={() => setNested(true)}>{'Open child'}</Button>
            }
          </div>
        )}
        confirmLabel={scenario === 'long-label' ? 'Continue with these changes and preserve the selected settings'.repeat(3) : 'Confirm'}
        cancelLabel="Cancel"
        onConfirm={() => {
          window.calls.confirm += 1;
          if (scenario === 'busy' || scenario === 'long-label') {
            setIsSpinning(true);
          } else {
            setIsOpen(false);
          }
        }}
        onCancel={onCancel}
      />
      <ConfirmModal
        isOpen={nested}
        title="Child confirmation"
        message="Confirm the child action?"
        confirmLabel="Confirm child"
        cancelLabel="Cancel child"
        onConfirm={() => {
          window.calls.nested += 1;
          setNested(false);
        }}
        onCancel={() => setNested(false)}
      />
    </div>
  );
}

function GroupedFooterFixture() {
  return (
    <Modal isOpen={true} onModalClose={() => {}}>
      <ModalContent onModalClose={() => {}}>
        <ModalHeader>{'Import'}</ModalHeader>
        <ModalBody>{'Grouped footer layout'}</ModalBody>
        <ModalFooter className={importStyles.footer}>
          <div className={importStyles.leftButtons}>
            <Button>{'Select'}</Button>
            <Button>{'Edit'}</Button>
          </div>
          <div className={importStyles.rightButtons}>
            <Button>{'Cancel'}</Button>
            <Button>{'Import'}</Button>
          </div>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

fetchTranslations().then(() => {
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        {
          scenario === 'settings' ?
            <Switch>
              <Route path="/settings/general" component={SettingsPage} />
              <Route path="/other" render={() => <div>{'Other page'}</div>} />
            </Switch> : null
        }
        {
          scenario !== 'settings' &&
            (scenario === 'grouped-footer' ? <GroupedFooterFixture /> : <ConfirmationFixture />)
        }
      </Router>
    </Provider>,
    document.getElementById('root')
  );
  window.fixtureReady = true;
});
