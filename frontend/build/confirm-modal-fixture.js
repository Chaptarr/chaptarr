import { createMemoryHistory } from 'history';
import React, { useEffect, useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Route, Router, Switch } from 'react-router-dom';
import { createStore } from 'redux';
import TextInput from 'Components/Form/TextInput';
import { shortcuts } from 'Components/keyboardShortcuts';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import MenuItem from 'Components/Menu/MenuItem';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import importStyles from 'InteractiveImport/Interactive/InteractiveImportModalContent.css';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import UnmappedFilesTable from 'UnmappedFiles/UnmappedFilesTable';
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
window.fixtureShortcuts = shortcuts;

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
                  <div contentEditable={true} suppressContentEditableWarning={true}>{'Editable'}</div>
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
        message={<div data-testid="child-content" tabIndex={0}>{'Confirm the child action?'}</div>}
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

function SendLogsFixture() {
  return (
    <>
      <UnmappedRetryFixture />
      <MenuItem data-testid="disabled-button" title="Disabled reason"
        isDisabled={true} onPress={() => window.logUploadCalls.push('button')}
      >
        {'Disabled button control'}
      </MenuItem>
      <MenuItem data-testid="disabled-link" title="Disabled reason"
        isDisabled={true} to="https://example.test/disabled"
      >
        {'Disabled link control'}
      </MenuItem>
    </>
  );
}

function UnmappedRetryFixture() {
  const query = new URLSearchParams(window.location.search);
  const groups = Number(query.get('groups') || 6);
  const parts = Number(query.get('parts') || 1);
  const items = Array.from({ length: groups * parts }, (_, i) => ({
    id: i + 1,
    path: `/unmapped/Book ${Math.floor(i / parts) + 1}/Part ${i + 1}.mp3`,
    importUnitKey: `audio:${Math.floor(i / parts)}`,
    size: 1000,
    quality: { quality: { id: 1, name: 'MP3' } }
  }));
  items.push({ id: 1000, path: '/unmapped/Ebook.epub', importUnitKey: 'ebook:1', size: 1000 });
  window.retryCalls = [];
  window.logUploadCalls = [];

  return (
    <UnmappedFilesTable
      ref={(table) => {
        window.unmappedTable = table;
      }}
      items={items}
      columns={[]}
      isFetching={false}
      isPopulated={true}
      isDeleting={false}
      isSendingLogs={false}
      isRefreshingFiles={false}
      isRetryMatching={false}
      onTableOptionChange={() => {}}
      onSortPress={() => {}}
      deleteUnmappedFile={() => {}}
      deleteUnmappedFiles={() => {}}
      fetchUnmappedFiles={() => {}}
      onRefreshUnmappedFilesPress={() => {}}
      onRetryUnmappedMatchPress={(mediaType, selection) => window.retryCalls.push({ mediaType, selection })}
      previewMatchingLogsForReview={() => window.logUploadCalls.push('preview')}
      sendMatchingLogsForReview={() => window.logUploadCalls.push('send')}
    />
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
  const fixtures = { 'send-logs': SendLogsFixture, 'unmapped-retry': UnmappedRetryFixture, 'grouped-footer': GroupedFooterFixture };
  const Fixture = fixtures[scenario] || ConfirmationFixture;
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
            <Fixture />
        }
      </Router>
    </Provider>,
    document.getElementById('root')
  );
  window.fixtureReady = true;
});
