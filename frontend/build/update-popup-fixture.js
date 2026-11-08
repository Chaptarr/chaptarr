import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { applyMiddleware, createStore } from 'redux';
import AppUpdatedModalContentConnector from 'App/AppUpdatedModalContentConnector';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

window.renderPopup = (items, prevVersion, theme, version = '0.9.991', error = null) => {
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  Object.entries(themes[theme]).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value));
  const store = createStore((state = {
    app: { version, prevVersion, dimensions: { width: window.innerWidth, height: window.innerHeight, isSmallScreen: window.innerWidth < 768 } },
    system: { updates: { isPopulated: true, error, items } }
  }) => state, applyMiddleware(() => (next) => (action) => {
    // Fixture supplies a settled API snapshot; still exercise the real connector/navigation.
    return typeof action === 'function' ? undefined : next(action);
  }));
  ReactDOM.render(
    <Provider store={store}>
      <AppUpdatedModalContentConnector onModalClose={() => {
        window.closed = true;
      }}
      />
    </Provider>, document.getElementById('root')
  );
};
fetchTranslations().then(() => {
  window.fixtureReady = true;
});
