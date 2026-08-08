import { createBrowserHistory } from 'history';
import React from 'react';
import { render } from 'react-dom';
import createAppStore from 'Store/createAppStore';
import App from './App/App';
import initializeChunkCleanup from './Utilities/chunkCleanup';
import checkStorageVersion from './Utilities/versionGate';

import 'Diag/ConsoleApi';

export async function bootstrap() {
  // Initialize chunk cleanup detection FIRST
  initializeChunkCleanup();

  // Check storage version before initializing the app
  checkStorageVersion();

  const history = createBrowserHistory();
  const store = createAppStore(history);

  render(
    <App store={store} history={history} />,
    document.getElementById('root')
  );
}
