import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import NotificationsConnector from 'Settings/Notifications/Notifications/NotificationsConnector';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

let store = null;

window.setNotificationFixtureTheme = (theme) => {
  Object.entries(themes[theme]).forEach(([key, value]) => {
    document.documentElement.style.setProperty(`--${key}`, value);
  });
};

window.renderNotificationFixture = () => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);

  const history = createMemoryHistory();
  store = createAppStore(history);
  store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
  store.dispatch(set({ section: 'tags', isPopulated: true, items: [] }));

  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <NotificationsConnector />
      </Router>
    </Provider>, root
  );
};

window.getNotificationFixtureState = () => store.getState().settings.notifications;

fetchTranslations().then(() => {
  window.fixtureReady = true;
});
