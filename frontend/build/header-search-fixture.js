import React from 'react';
import ReactDOM from 'react-dom';
import AuthorSearchInput from 'Components/Page/Header/AuthorSearchInput';
import orderAuthorSearchResults from 'Search/orderAuthorSearchResults';
import { getSearchResults } from 'Store/Actions/searchActions';
import createExistingAuthorSelector from 'Store/Selectors/createExistingAuthorSelector';
import themes from 'Styles/Themes';
import createBookSearchFilter from 'Utilities/Book/createBookSearchFilter';
import createTextSearch from 'Utilities/String/createTextSearch';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

window.renderHeaderSearch = (theme = 'dark') => {
  Object.entries(themes[theme]).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value));
  window.headerNavigations = [];
  ReactDOM.render(
    <AuthorSearchInput
      onGoToAuthor={(id) => window.headerNavigations.push(['author', id])}
      onGoToBook={(id) => window.headerNavigations.push(['book', id])}
      onGoToAddNewAuthor={(query, provider) => window.headerNavigations.push(['add', query, provider])}
    />, document.getElementById('root')
  );
};
window.unmountHeaderSearch = () => ReactDOM.unmountComponentAtNode(document.getElementById('root'));
fetchTranslations().then(() => {
  window.fixtureReady = true;
});

window.searchSurface = { createTextSearch, createBookSearchFilter, createExistingAuthorSelector, orderAuthorSearchResults };
window.searchActions = [];
window.runAddNewSearch = (payload) => getSearchResults(payload)((action) => window.searchActions.push(action), () => ({}));
window.readAddNewResults = () => window.searchActions.flatMap((action) => (action.meta?.batch ? action.payload : [action]))
  .filter((action) => action.payload?.section === 'search' && action.payload?.data).at(-1)?.payload.data;
