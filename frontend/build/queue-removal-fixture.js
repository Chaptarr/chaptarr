import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import Queue from 'Activity/Queue/Queue';
import QueueRow from 'Activity/Queue/QueueRow';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';

window.renderQueueRemovalFixture = (states, bulk) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  const noop = () => {};
  const ref = React.createRef();
  const items = states.map((state, index) => {
    const pending = state === 'delay' || state === 'downloadClientUnavailable';
    return { id: index + 1, downloadId: pending ? undefined : `download-${index}`, trackedDownloadState: pending ? undefined : state, status: pending ? state : 'completed' };
  });
  store.dispatch(set({ section: 'app', dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: innerWidth <= 768 } }));
  window.queueRemovalCalls = [];
  const record = (value) => window.queueRemovalCalls.push(value);
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        {bulk ? (
          <Queue
            ref={ref} items={items}
            columns={[]}
            isFetching={false} isPopulated={false}
            isAuthorFetching={false} isAuthorPopulated={false}
            isBooksFetching={false} isBooksPopulated={false}
            isGrabbing={false} isRemoving={false}
            isRetryingImport={false}
            autoAddMissingAuthorsFromCompletedDownloads={false}
            isAutoAddMissingAuthorsPopulated={true} isAutoAddMissingAuthorsSaving={false}
            isRefreshMonitoredDownloadsExecuting={false}
            onRefreshPress={noop} onRetryImportSelectedPress={noop}
            onAutoAddAuthorsPress={noop} onGrabSelectedPress={noop}
            onRemoveSelectedPress={record}
          />
        ) : (
          <table>
            <tbody>
              <QueueRow
                ref={ref} {...items[0]}
                title="Dune" trackedDownloadStatus="warning"
                quality={{ quality: { id: 1, name: 'Any' }, revision: { version: 1 } }}
                customFormatScore={0} protocol="usenet"
                columns={[]}
                showRelativeDates={false} shortDateFormat="MMM D YYYY"
                timeFormat="HH:mm"
                onSelectedChange={noop} onGrabPress={noop}
                onCancelConversionPress={noop}
                onRetryImportPress={noop} onRemoveQueueItemPress={record}
                onQueueRowModalOpenOrClose={noop}
              />
            </tbody>
          </table>
        )}
      </Router>
    </Provider>, root
  );
  if (bulk) {
    items.forEach((item) => ref.current.onSelectedChange({ id: item.id, value: true }));
  }
  window.openQueueRemoval = () => (bulk ? ref.current.onRemoveSelectedPress() : ref.current.onRemoveQueueItemPress());
  window.refreshQueueRemoval = () => ref.current.forceUpdate();
  window.openQueueRemoval();
};
