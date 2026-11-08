import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import HistoryDetails from 'Activity/History/Details/HistoryDetails';
import DeleteBook from 'Book/Delete/DeleteBookModalContent';
import DeleteBooks from 'Book/Editor/Delete/DeleteBookModalContent';
import DeleteBookFilesModal from 'BookFile/Editor/DeleteBookFilesModal';
import Modal from 'Components/Modal/Modal';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import UnmappedFilesTable from 'UnmappedFiles/UnmappedFilesTable';
import UnmappedFilesTableRow from 'UnmappedFiles/UnmappedFilesTableRow';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const history = createMemoryHistory();
const store = createAppStore(history);
window.calls = { confirm: 0, cancel: 0 };
window.renderDeletion = (ids, theme = 'dark', isOpen = true) => {
  Object.entries(themes[theme]).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value));
  ReactDOM.render(
    <Provider store={store}>
      <DeleteBookFilesModal isOpen={isOpen} fileIds={ids}
        onConfirm={() => {
          window.calls.confirm++;
        }}
        onCancel={() => {
          window.calls.cancel++;
          window.renderDeletion(ids, theme, false);
        }}
      />
    </Provider>, document.getElementById('root')
  );
};
window.renderBookDeletion = (bulk = false) => {
  const close = () => ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  const onDelete = () => {
    window.calls.confirm++;
  };
  ReactDOM.render(
    <Provider store={store}>
      <Modal isOpen={true} onModalClose={close}>
        {bulk ?
          <DeleteBooks bookIds={[7, 8]} book={[{ title: 'Dune' }, { title: 'Dune Messiah' }]}
            selectedCount={2} onModalClose={close}
            onDeleteSelectedPress={onDelete}
          /> :
          <DeleteBook bookId={7} title="Dune"
            mediaType="audiobook" onModalClose={close}
            onDeletePress={onDelete}
            siblingBook={{ siblings: [{ bookId: 8, title: 'Dune', mediaType: 'ebook' }] }}
          />}
      </Modal>
    </Provider>, document.getElementById('root')
  );
};
window.renderUnmappedDeletion = (bulk) => {
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  window.unmappedDeleted = [];
  const noop = () => {};
  const file = { id: 51, path: '/downloads/Dune.epub', size: 100, dateAdded: '2026-11-01T00:00:00Z', quality: {} };
  const columns = [{ name: 'path', isVisible: true }, { name: 'actions', isVisible: true }];
  const deleteFile = (id) => window.unmappedDeleted.push(id);
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        {bulk ?
          <UnmappedFilesTable
            ref={(value) => {
              window.unmappedTable = value;
            }}
            items={[file]}
            columns={columns}
            isFetching={false}
            isPopulated={true}
            isDeleting={false}
            isSendingLogs={false}
            isRefreshingFiles={false}
            isRetryMatching={false}
            onTableOptionChange={noop}
            onSortPress={noop}
            deleteUnmappedFile={deleteFile}
            deleteUnmappedFiles={(ids) => ids.forEach(deleteFile)}
            fetchUnmappedFiles={noop}
            onRefreshUnmappedFilesPress={noop}
            onRetryUnmappedMatchPress={noop}
            previewMatchingLogsForReview={noop}
            sendMatchingLogsForReview={noop}
          /> :
          <div id="unmapped-row">
            <UnmappedFilesTableRow
              {...file}
              columns={columns}
              bookUnitFileIds={[51]}
              importUnitRoot="/downloads"
              onSelectedChange={noop}
              deleteUnmappedFile={deleteFile}
              onImportComplete={noop}
            />
          </div>}
      </Router>
    </Provider>, document.getElementById('root')
  );
  if (bulk) {
    window.unmappedTable.onSelectAllChange({ value: true });
  }
};
fetchTranslations().then(() => {
  window.fixtureReady = true;
});

window.renderImportedHistory = (data) => {
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  ReactDOM.render(
    <HistoryDetails
      eventType="bookFileImported"
      sourceTitle="Requested release"
      data={data}
      shortDateFormat="yyyy-MM-dd"
      timeFormat="HH:mm"
    />,
    document.getElementById('root')
  );
};
