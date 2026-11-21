import { createMemoryHistory } from 'history';
import React, { useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import NamingModal from 'Settings/MediaManagement/Naming/NamingModal';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

function Fixture() {
  const [pattern, setPattern] = useState('{Book Title}{ - Smart Narrator}{ (PartNumber:smart)}');
  const [open, setOpen] = useState(true);
  window.openNamingFixture = () => setOpen(true);
  window.setNamingPattern = setPattern;
  window.namingPattern = pattern;
  return (
    <NamingModal
      name="standardBookFormat"
      value={pattern}
      isOpen={open}
      book={true}
      additional={true}
      advancedSettings={false}
      onInputChange={({ value }) => setPattern(value)}
      onModalClose={() => setOpen(false)}
    />
  );
}

window.renderNamingFixture = (theme) => {
  Object.entries(themes[theme]).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value));
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const store = createAppStore(createMemoryHistory());
  store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
  ReactDOM.render(<Provider store={store}><Fixture /></Provider>, root);
};

fetchTranslations().then(() => {
  window.fixtureReady = true;
});
