import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import QueueRow from 'Activity/Queue/QueueRow';
import InteractiveImportModal from 'InteractiveImport/InteractiveImportModal';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

window.renderManualImportFixture = (themeName, folderMode = false) => {
  Object.entries(themes[themeName]).forEach(([key, value]) => {
    document.documentElement.style.setProperty(`--${key}`, value);
  });

  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);

  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({
    section: 'app',
    dimensions: { width: window.innerWidth, height: window.innerHeight, isSmallScreen: window.innerWidth <= 768 }
  }));
  store.dispatch(set({
    section: 'settings.mediaManagement',
    isPopulated: true,
    item: {
      bookMatchingStrictness: 'balanced',
      usePathAsTagsFallback: true
    }
  }));

  window.manualImportState = () => store.getState().interactiveImport;
  window.manualImportCloseCount = 0;

  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        {
          folderMode ?
            <InteractiveImportModal
              isOpen={true}
              folder="/downloads"
              onModalClose={() => {
                window.manualImportCloseCount++;
              }}
            /> :
            <table>
              <tbody>
                <QueueRow
                  id={999}
                  downloadId="queue-download"
                  title="Queued book"
                  status="completed"
                  trackedDownloadStatus="warning"
                  trackedDownloadState="importBlocked"
                  quality={{ quality: { id: 1, name: 'Any' }, revision: { version: 1 } }}
                  customFormatScore={0}
                  protocol="usenet"
                  showRelativeDates={false}
                  shortDateFormat="MMM D YYYY"
                  timeFormat="HH:mm"
                  columns={[{ name: 'actions', isVisible: true }]}
                  onSelectedChange={() => {}}
                  onGrabPress={() => {}}
                  onCancelConversionPress={() => {}}
                  onRetryImportPress={() => {}}
                  onRemoveQueueItemPress={() => {}}
                  onQueueRowModalOpenOrClose={(isOpen) => {
                    if (!isOpen) {
                      window.manualImportCloseCount++;
                    }
                  }}
                />
              </tbody>
            </table>
        }
      </Router>
    </Provider>, root);
};

fetchTranslations().then(() => {
  window.manualImportFixtureReady = true;
});
