import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { createStore } from 'redux';
import Modal from 'Components/Modal/Modal';
import RetagPreviewModalContent from 'Retag/RetagPreviewModalContent';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const store = createStore((state = {
  settings: { advancedSettings: false },
  app: { dimensions: { width: window.innerWidth, height: window.innerHeight, isSmallScreen: window.innerWidth < 768 } }
}) => state);
const scenario = new URLSearchParams(window.location.search).get('scenario');
const skipped = { id: 2, bookFileId: 2, path: '/library/unregistered.epub', skipReason: 'notInCalibre', changes: [] };
const row = (id) => ({ id, bookFileId: id, path: `/library/${id}.mp3`, changes: [{ field: 'Title', oldValue: 'Old', newValue: 'New' }] });
const items = scenario === 'unregistered' ? [skipped] : [row(1), skipped, { ...row(3), isFileSymlink: true },
  { id: 4, bookFileId: 4, path: '/library/offline.epub', changes: [], skipReason: 'libraryUnavailable', calibreLibrary: 'http://server:8080/ [books]', retryAfter: '2000-11-11T12:01:00Z', retryNow: false },
  { ...row(5), skipReason: 'previewFailed' },
  { ...row(7), path: '/library/ebook.epub', isCalibreFormat: true, skipReason: 'previewFailed' },
  { ...row(8), path: '/library/error.epub', skipReason: 'libraryPreviewFailed', calibreLibrary: 'broken' },
  { ...row(9), skipReason: 'missingRootFolder' },
  { ...row(10), skipReason: 'invalidCalibreSettings', rootFolderPath: '/invalid-root' },
  { ...row(11), skipReason: 'rootNotLinkedToCalibre', rootFolderPath: '/unlinked-root' },
  { ...row(12), skipReason: 'futureReason' },
  { ...row(14), skipReason: 'libraryUnavailable', calibreLibrary: 'zero-window', retryAfter: '2099-01-01T00:00:00Z', retryNow: true },
  { ...row(6), skipReason: 'missingCalibreBook', calibreLibrary: 'missing' }];
window.calls = [];
function setPreview(component) {
  window.retag = component;
}
fetchTranslations().then(() => {
  ReactDOM.render(
    <Provider store={store}>
      <Modal isOpen={true} onModalClose={() => {}}>
        <RetagPreviewModalContent
          ref={setPreview}
          items={items}
          path="/library"
          shortDateFormat="YYYY-MM-DD"
          timeFormat="HH:mm"
          isFetching={false}
          isPopulated={true}
          onModalClose={() => {}}
          onRetagPress={(...args) => window.calls.push(args)}
        />
      </Modal>
    </Provider>, document.getElementById('root'));
  window.fixtureReady = true;
});
