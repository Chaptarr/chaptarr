import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { createStore } from 'redux';
import MediaManagementConnector from 'Settings/MediaManagement/MediaManagementConnector';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const MediaManagement = MediaManagementConnector.WrappedComponent;
const noop = () => {};
const store = createStore((state = { app: { dimensions: { width: window.innerWidth } } }, action) => {
  if (action.type === 'fixture/viewportChanged') {
    return {
      ...state,
      app: {
        ...state.app,
        dimensions: {
          ...state.app.dimensions,
          width: action.width
        }
      }
    };
  }

  return state;
});

class Fixture extends Component {

  state = {
    selectedScope: this.props.selectedScope,
    otherSettingPending: this.props.otherSettingPending
  };

  getSettings() {
    const {
      savedScope
    } = this.props;

    const {
      selectedScope,
      otherSettingPending
    } = this.state;

    const coverDownloadScope = {
      value: selectedScope,
      errors: [],
      warnings: []
    };

    if (selectedScope !== savedScope) {
      coverDownloadScope.previousValue = savedScope;
      coverDownloadScope.pending = true;
    }

    return {
      coverDownloadScope,
      bookMatchingStrictness: {
        value: otherSettingPending ? 'strict' : 'balanced',
        previousValue: otherSettingPending ? 'balanced' : undefined,
        errors: [],
        warnings: []
      },
      usePathAsTagsFallback: {
        value: !otherSettingPending,
        errors: [],
        warnings: []
      },
      autoAddMissingAuthorsFromCompletedDownloads: {
        value: false,
        errors: [],
        warnings: []
      },
      autoUnmonitorPreviouslyDownloadedBooks: {
        value: false,
        errors: [],
        warnings: []
      }
    };
  }

  setMediaManagementSettingsValue = ({ name, value }) => {
    if (name === 'coverDownloadScope') {
      this.setState({ selectedScope: value });
    }
  };

  saveMediaManagementSettings = () => {
    window.savedCalls.push('mediaManagement');

    return {
      done: (callback) => window.pendingSaveCallbacks.push(callback)
    };
  };

  saveNamingSettings = () => {
    window.savedCalls.push('naming');
  };

  invalidateBookCoverScopeResources = () => {
    window.invalidations.push('coverScope');
  };

  render() {
    const settings = this.getSettings();
    const hasPendingChanges = settings.coverDownloadScope.value !== this.props.savedScope || this.state.otherSettingPending;

    return (
      <Provider store={store}>
        <div style={{ height: '100vh', display: 'flex', flexDirection: 'column' }}>
          <MediaManagement
            advancedSettings={false}
            isFetching={false}
            error={null}
            settings={settings}
            hasSettings={true}
            hasPendingChanges={hasPendingChanges}
            isWindows={false}
            isSaving={false}
            saveError={null}
            fetchMediaManagementSettings={noop}
            setMediaManagementSettingsValue={this.setMediaManagementSettingsValue}
            saveMediaManagementSettings={this.saveMediaManagementSettings}
            saveNamingSettings={this.saveNamingSettings}
            invalidateBookCoverScopeResources={this.invalidateBookCoverScopeResources}
            clearPendingChanges={noop}
            fetchDownloadClients={noop}
            fetchRootFolders={noop}
          />
        </div>
      </Provider>
    );
  }
}

function renderFixture({ savedScope = 'allBooks', selectedScope = savedScope, otherSettingPending = false } = {}) {
  window.savedCalls = [];
  window.invalidations = [];
  window.pendingSaveCallbacks = [];
  window.resolveMediaSaveSuccess = () => {
    window.pendingSaveCallbacks.forEach((callback) => callback());
    window.pendingSaveCallbacks = [];
  };
  store.dispatch({ type: 'fixture/viewportChanged', width: window.innerWidth });
  window.fixtureReady = true;
  ReactDOM.render(
    <Fixture
      key={`${savedScope}-${selectedScope}-${otherSettingPending}-${Date.now()}`}
      savedScope={savedScope}
      selectedScope={selectedScope}
      otherSettingPending={otherSettingPending}
    />,
    document.getElementById('root')
  );
}

Fixture.propTypes = {
  savedScope: PropTypes.string.isRequired,
  selectedScope: PropTypes.string.isRequired,
  otherSettingPending: PropTypes.bool.isRequired
};

Object.entries(themes.dark).forEach(([key, value]) => {
  document.documentElement.style.setProperty(`--${key}`, value);
});

fetchTranslations().then(() => {
  window.renderMediaManagementFixture = renderFixture;
  renderFixture();
});
