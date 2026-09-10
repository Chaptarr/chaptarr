import React, { useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { createStore } from 'redux';
import headerStyles from 'Author/Details/AuthorDetails.css';
import AuthorDetailsHeader from 'Author/Details/AuthorDetailsHeader';
import MediaTypeTripleToggle from 'Author/Index/MediaTypeTripleToggle';
import IconButton from 'Components/Link/IconButton';
import Menu from 'Components/Menu/Menu';
import MenuContent from 'Components/Menu/MenuContent';
import MenuItem from 'Components/Menu/MenuItem';
import ToolbarMenuButton from 'Components/Menu/ToolbarMenuButton';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import SwipeHeader from 'Components/Swipe/SwipeHeader';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import { icons } from 'Helpers/Props';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

const h = React.createElement;
const viewportState = () => ({ settings: { qualityProfiles: { items: [] } }, app: { dimensions: { width: innerWidth, isSmallScreen: innerWidth <= 768 } } });
const store = createStore(viewportState);
window.addEventListener('resize', () => store.dispatch({ type: 'resize' }));
const columns = [{ name: 'title', label: 'Title', isVisible: true }];

function Fixture() {
  const [format, setFormat] = useState('all');
  const action = (label) => h(PageToolbarButton, { key: label, label, iconName: icons.SEARCH, onPress: () => {} });
  const menu = (label) => h(Menu, { key: label, alignMenu: 'right' },
    h(ToolbarMenuButton, { iconName: icons.FILTER, text: label }),
    h(MenuContent, null, h(MenuItem, { onPress: () => {} }, `${label} choice`)));

  // Match the index's non-collapsing right section, not just its total button count.
  return h(PageToolbar, { controls: h(MediaTypeTripleToggle, { selectedMediaType: format, onMediaTypeChange: setFormat }) },
    h(PageToolbarSection, null, action('Update All'), action('RSS Sync'), h(PageToolbarSeparator), action('Author Editor')),
    h(PageToolbarSection, { alignContent: 'right', collapseButtons: false },
      h(TableOptionsModalWrapper, { columns, onTableOptionChange: () => {} }, action('Options')),
      h(PageToolbarSeparator), menu('View'), menu('Sort'), menu('Filter')));
}

window.renderToolbarFixture = async() => {
  await fetchTranslations();
  ReactDOM.render(h(Provider, { store }, h(Fixture)), document.getElementById('root'));
};

window.renderHeaderFixture = async() => {
  await fetchTranslations();
  const header = (width) => h(AuthorDetailsHeader, {
    id: 1, authorName: 'Example Author', width, ratings: { value: 0 },
    statistics: {}, monitored: true, status: 'continuing', overview: '',
    links: [], images: [], alternateTitles: [], tags: [], isSaving: false,
    isSmallScreen: innerWidth <= 768, onMonitorTogglePress: () => {}
  });
  ReactDOM.render(h(Provider, { store }, h(SwipeHeader, {
    className: headerStyles.header, currentComponent: header,
    nextComponent: header, prevComponent: header, nextLink: '/', prevLink: '/',
    isSmallScreen: innerWidth <= 768, onGoTo: () => {}
  }, h('div', { className: headerStyles.authorNavigationButtons },
    h(IconButton, { className: headerStyles.authorUpButton, name: icons.ARROW_UP, size: 30, onPress: () => {} })
  ))), document.getElementById('root'));
};
