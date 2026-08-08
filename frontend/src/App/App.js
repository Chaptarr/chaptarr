import { ConnectedRouter } from 'connected-react-router';
import PropTypes from 'prop-types';
import React from 'react';
import DocumentTitle from 'react-document-title';
import { Provider } from 'react-redux';
import PageConnector from 'Components/Page/PageConnector';
import AppErrorBoundary from './AppErrorBoundary';
import ApplyTheme from './ApplyTheme';
import AppRoutes from './AppRoutes';

function App({ store, history }) {
  return (
    <DocumentTitle title={window.Chaptarr.instanceName}>
      <Provider store={store}>
        <ConnectedRouter history={history}>
          <AppErrorBoundary>
            <ApplyTheme>
              <PageConnector>
                <AppRoutes app={App} />
              </PageConnector>
            </ApplyTheme>
          </AppErrorBoundary>
        </ConnectedRouter>
      </Provider>
    </DocumentTitle>
  );
}

App.propTypes = {
  store: PropTypes.object.isRequired,
  history: PropTypes.object.isRequired
};

export default App;
