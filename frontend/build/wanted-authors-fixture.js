import { createMemoryHistory } from 'history';
import React, { Component } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import AppRoutes from 'App/AppRoutes';
import PageHeader from 'Components/Page/Header/PageHeader';
import pageStyles from 'Components/Page/Page.css';
import PageTransitionWrapper from 'Components/Page/PageTransitionWrapper';
import PageSidebar from 'Components/Page/Sidebar/PageSidebar';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const initialPath = new URLSearchParams(window.location.search).get('surface') === 'missing' ? '/wanted/missing' : '/wanted/authors';
const history = createMemoryHistory({ initialEntries: [initialPath] });
const store = createAppStore(history);
store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
store.dispatch(set({ section: 'settings.ui', item: {
  showRelativeDates: false, shortDateFormat: 'MMM D YYYY', longDateFormat: 'dddd, MMMM D YYYY', timeFormat: 'HH:mm'
} }));
store.dispatch(set({ section: 'authors', isFetching: false, isPopulated: true, itemMap: { 1: 0 }, items: [
  { id: 1, authorName: 'Reference Writer', sortName: 'referencewriter', titleSlug: 'reference-writer' }
] }));

class Fixture extends Component {
  state = { location: history.location, isSidebarVisible: window.innerWidth > 768 };

  componentDidMount() {
    this.unlisten = history.listen((location) => this.setState({ location }));
  }

  componentWillUnmount() {
    this.unlisten();
  }

  render() {
    return (
      <div className={pageStyles.page}>
        <PageHeader onSidebarToggle={() => this.setState({ isSidebarVisible: !this.state.isSidebarVisible })} />
        <div className={pageStyles.main}>
          <PageSidebar
            location={this.state.location}
            isSmallScreen={window.innerWidth <= 768}
            isSidebarVisible={this.state.isSidebarVisible}
            onSidebarVisibleChange={(isSidebarVisible) => this.setState({ isSidebarVisible })}
          />
          <PageTransitionWrapper>
            <AppRoutes app={store.getState().app} />
          </PageTransitionWrapper>
        </div>
      </div>
    );
  }
}

window.getWantedAuthorsState = () => store.getState().wanted.authors;
window.getFixturePath = () => history.location.pathname;
window.goToFixturePath = (pathname) => history.push(pathname);
window.setFixtureTheme = (theme) => Object.entries(themes[theme]).forEach(([key, value]) => {
  document.documentElement.style.setProperty(`--${key}`, value);
});
window.setFixtureTheme(window.innerWidth <= 768 ? 'dark' : 'light');

fetchTranslations().then(() => {
  ReactDOM.render(<Provider store={store}><Router history={history}><Fixture /></Router></Provider>, document.getElementById('root'));
  window.fixtureReady = true;
});
