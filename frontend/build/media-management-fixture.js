import { createMemoryHistory } from 'history';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import { createStore } from 'redux';
import QueueConnector from 'Activity/Queue/QueueConnector';
import MetadataProfileSelectInputConnector from 'Components/Form/MetadataProfileSelectInputConnector';
import QualityProfileSelectInputConnector from 'Components/Form/QualityProfileSelectInputConnector';
import TagInputConnector from 'Components/Form/TagInputConnector';
import Button from 'Components/Link/Button';
import pageStyles from 'Components/Page/Page.css';
import MessagesConnector from 'Components/Page/Sidebar/Messages/MessagesConnector';
import InteractiveImportSelectFolderModalContentConnector from 'InteractiveImport/Folder/InteractiveImportSelectFolderModalContentConnector';
import InteractiveImportModalContentConnector from 'InteractiveImport/Interactive/InteractiveImportModalContentConnector';
import AddNewBookModalContentConnector from 'Search/Book/AddNewBookModalContentConnector';
import GeneralSettingsConnector from 'Settings/General/GeneralSettingsConnector';
import ProxySettingsModal from 'Settings/General/ProxySettingsModal';
import MediaManagementConnector from 'Settings/MediaManagement/MediaManagementConnector';
import EditRootFolderModal from 'Settings/MediaManagement/RootFolder/EditRootFolderModal';
import { clearPendingChanges, set } from 'Store/Actions/baseActions';
import { executeCommand, finishCommand, updateCommand } from 'Store/Actions/commandActions';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import { cloneMetadataProfile, fetchMetadataProfiles, fetchQualityProfiles, saveGeneralSettings, saveGeneralSettingsFields, saveMediaManagementSettings, saveMediaManagementSettingsFields, saveMetadataProfile, setGeneralSettingsValue, setMediaManagementSettingsValue, setMetadataProfileValue } from 'Store/Actions/settingsActions';
import createAppStore from 'Store/createAppStore';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import themes from 'Styles/Themes';
import LogFilesConnector from 'System/Logs/Files/LogFilesConnector';
import QuickstartMatchingSection from 'System/Quickstart/QuickstartMatchingSection';
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

window.setFixtureTheme = (theme) => Object.entries(themes[theme]).forEach(([key, value]) => {
  document.documentElement.style.setProperty(`--${key}`, value);
});
window.setFixtureTheme('dark');

fetchTranslations().then(() => {
  window.renderMediaManagementFixture = renderFixture;
  renderFixture();
});

// eslint-disable-next-line react/no-multi-comp
class SettingsIsolationFixture extends Component {
  state = { showQuickstart: true, rootOpen: false, rootId: 1 };

  render() {
    return (
      <>
        <Button onPress={() => this.setState({ showQuickstart: !this.state.showQuickstart })}>
          {this.state.showQuickstart ? 'Leave Quickstart' : 'Return to Quickstart'}
        </Button>
        <Button onPress={() => this.setState({ rootOpen: true, rootId: 2 })}>{'Edit default root folder'}</Button>
        <Button onPress={() => this.setState({ rootOpen: true, rootId: 1 })}>{'Edit root folder'}</Button>
        <Button onPress={() => this.setState({ rootOpen: true, rootId: undefined })}>{'Add root folder'}</Button>
        {this.state.showQuickstart && <QuickstartMatchingSection markSectionInteracted={() => window.matchingCompletions++} />}
        <EditRootFolderModal
          id={this.state.rootId}
          isOpen={this.state.rootOpen}
          onSaveSuccess={() => window.rootCompletions++}
          onRootFolderAdded={() => window.rootAddCompletions++}
          onModalClose={() => this.setState({ rootOpen: false })}
        />
      </>
    );
  }
}

// eslint-disable-next-line react/no-multi-comp
class GeneralIsolationFixture extends Component {
  state = { proxyOpen: true };

  render() {
    return (<>
      <Button onPress={() => this.setState({ proxyOpen: true })}>{'Open proxy'}</Button>
      <LogFilesConnector />
      <ProxySettingsModal
        isOpen={this.state.proxyOpen}
        onModalClose={() => this.setState({ proxyOpen: false })}
      />
    </>);
  }
}

function loadProfiles(appStore) {
  appStore.dispatch(fetchQualityProfiles());
  appStore.dispatch(fetchMetadataProfiles());
  window.cloneMetadataProfile = (id) => {
    appStore.dispatch(cloneMetadataProfile({ id }));
    appStore.dispatch(saveMetadataProfile({}));
  };
  window.createMetadataProfile = (profileType) => {
    appStore.dispatch(setMetadataProfileValue({ name: 'name', value: 'New metadata profile' }));
    appStore.dispatch(setMetadataProfileValue({ name: 'profileType', value: profileType }));
    appStore.dispatch(saveMetadataProfile({}));
  };
  window.getMetadataProfiles = () => appStore.getState().settings.metadataProfiles;
  window.renderNullTags = () => ReactDOM.render(
    <Provider store={appStore}>
      <TagInputConnector name="tags" value={null}
        onChange={noop}
      />
    </Provider>, document.getElementById('root'));
  window.getAddBookPayload = (profiles) => {
    let result = null;
    const connector = new AddNewBookModalContentConnector.WrappedComponent({
      ...profiles, foreignBookId: 'hc:100', foreignAuthorId: 'hc:200',
      addBook: (payload) => {
        result = payload;
      }
    });
    connector.onAddBookPress(false, 'both');
    return result;
  };
}

window.renderSettingsIsolationFixture = (surface = 'quickstart', rootOverrides = {}) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  window.matchingCompletions = 0;
  window.rootCompletions = 0;
  window.rootAddCompletions = 0;
  const history = createMemoryHistory();
  const appStore = createAppStore(history);
  const schema = appStore.getState().settings.rootFolders.schema;
  appStore.dispatch(set({ section: 'settings.rootFolders', isPopulated: true, items: [
    { ...schema, id: 1, name: 'Books', path: '/data/audiobooks', folderType: 1, ...rootOverrides },
    { ...schema, id: 2, name: 'Other books', path: '/data/other', folderType: 1 }
  ] }));
  appStore.dispatch(set({ section: 'system.status', item: { appData: '/config', isWindows: false, mode: 'console', packageUpdateMechanism: 'builtin' } }));
  loadProfiles(appStore);
  appStore.dispatch(set({ section: 'tags', isPopulated: true, items: [{ id: 51, label: 'audio-tag' }, { id: 52, label: 'ebook-tag' }] }));
  window.getRootFolderState = () => appStore.getState().settings.rootFolders;
  window.getGeneralDraft = () => appStore.getState().settings.general.pendingChanges;
  window.getGeneralSettingsState = () => appStore.getState().settings.general;
  window.getSettingsCommands = () => appStore.getState().commands.items;
  window.finishedSettingsCommands = [];
  window.executeSettingsCommand = (command) => appStore.dispatch(executeCommand({
    ...command,
    commandFinished: (finished) => window.finishedSettingsCommands.push(finished)
  }));
  window.receiveSettingsCommand = (command) => {
    if (['completed', 'failed'].includes(command.status)) {
      appStore.dispatch(finishCommand(command));
    } else {
      appStore.dispatch(updateCommand(command));
    }
  };
  window.saveGeneralSetting = (changes, fieldsOnly = true) => appStore.dispatch(fieldsOnly ? saveGeneralSettingsFields(changes) : saveGeneralSettings(changes));
  window.setGeneralDraft = (name, value) => appStore.dispatch(setGeneralSettingsValue({ name, value }));
  window.getSettingsDraft = () => appStore.getState().settings.mediaManagement.pendingChanges;
  window.getSettingsState = () => appStore.getState().settings.mediaManagement;
  window.setSettingsPopulated = (isPopulated) => appStore.dispatch(set({ section: 'settings.mediaManagement', isPopulated }));
  window.saveSettings = (changes, fieldsOnly = true) => appStore.dispatch(fieldsOnly ? saveMediaManagementSettingsFields(changes) : saveMediaManagementSettings(changes));
  window.setSettingsDraft = (name, value) => appStore.dispatch(setMediaManagementSettingsValue({ name, value }));
  window.quickRefetches = 0;
  window.saveQuickSetting = (quickSurface, value) => {
    const mediaManagementSettings = createSettingsSectionSelector('mediaManagement')(appStore.getState());
    const props = {
      mediaManagementSettings,
      autoAddMissingAuthorsFromCompletedDownloads: !!mediaManagementSettings.settings.autoAddMissingAuthorsFromCompletedDownloads?.value,
      setMediaManagementSettingsValue: (payload) => appStore.dispatch(setMediaManagementSettingsValue(payload)),
      saveMediaManagementSettings: (payload) => appStore.dispatch(saveMediaManagementSettingsFields(payload))
    };
    if (quickSurface === 'queue') {
      const renderQueue = QueueConnector;
      const Queue = renderQueue({ history: { action: 'PUSH' } }).type.WrappedComponent;
      const connector = new Queue(props);
      connector.setState = noop;
      connector.onAutoAddAuthorsPress(value);
    } else {
      const Connector = quickSurface === 'interactive' ? InteractiveImportModalContentConnector.WrappedComponent : InteractiveImportSelectFolderModalContentConnector.WrappedComponent;
      const connector = new Connector(props);
      connector.setState = noop;
      connector.refetchItems = () => window.quickRefetches++;
      connector.onPathFallbackChange({ value });
    }
  };
  window.saveCoverSetting = () => appStore.dispatch(saveMediaManagementSettingsFields({ coverDownloadScope: 'libraryBooks' }));
  window.batchedAcceptAndLeaveMatching = () => ReactDOM.unstable_batchedUpdates(() => {
    appStore.dispatch(set({ section: 'settings.mediaManagement',
      item: { ...appStore.getState().settings.mediaManagement.item, bookMatchingStrictness: 'aggressive', usePathAsTagsFallback: true },
      saveError: { message: 'Other section failed' }
    }));
    ReactDOM.unmountComponentAtNode(root);
  });
  window.clearAllSettingsDrafts = () => appStore.dispatch(clearPendingChanges({ section: 'settings.mediaManagement' }));
  window.runSaveObserverControl = () => {
    const save = createSaveHandler('settings.mediaManagement', '/config/mediamanagement');
    window.observerSaveResults = [];
    window.observerDispatchErrors = 0;
    let armed = true;
    const dispatch = (action) => {
      const result = appStore.dispatch(action);
      if (armed && !appStore.getState().settings.mediaManagement.isSaving) {
        armed = false;
        window.observerDispatchErrors++;
        throw new Error('Save observer failed');
      }
      return result;
    };
    save(appStore.getState, { coverDownloadScope: 'libraryBooks' }, dispatch, true)
      .done(() => window.observerSaveResults.push('first:done'))
      .fail(() => window.observerSaveResults.push('first:fail'));
    save(appStore.getState, { usePathAsTagsFallback: false }, dispatch, true)
      .done(() => window.observerSaveResults.push('next:done'));
  };
  let content = <SettingsIsolationFixture />;
  const renderContent = () => ReactDOM.render(<Provider store={appStore}><Router history={history}>{content}</Router></Provider>, root);
  if (surface === 'settings') {
    content = <MediaManagementConnector />;
  } else if (surface === 'general-settings') {
    content = <><GeneralSettingsConnector /><MessagesConnector /></>;
  } else if (surface === 'general') {
    content = <GeneralIsolationFixture />;
  } else if (surface === 'logs') {
    content = <LogFilesConnector />;
  } else if (surface === 'queue') {
    content = <div className={pageStyles.page}><div className={pageStyles.main}><QueueConnector history={history} /></div></div>;
  } else if (surface === 'interactive') {
    content = <InteractiveImportModalContentConnector folder="/data/import" onModalClose={noop} />;
  } else if (surface === 'folder') {
    content = <InteractiveImportSelectFolderModalContentConnector onModalClose={noop} onFolderSelect={noop} />;
  }
  renderContent();
};

// eslint-disable-next-line react/no-multi-comp
class ProfileContractFixture extends Component {
  state = { quality: this.props.quality, metadata: this.props.metadata };

  onChange = ({ name, value }) => {
    window.profileChanges.push({ name, value, type: typeof value });
    this.setState({ [name]: value });
  };

  render() {
    const shared = { profileType: 'audiobook', includeNone: this.props.includeNone,
      includeNoChange: true, includeNoChangeDisabled: false, includeMixed: true,
      isDisabled: this.props.isDisabled, onChange: this.onChange };
    return (<>
      <QualityProfileSelectInputConnector
        {...shared}
        name="quality"
        value={this.state.quality}
      />
      <MetadataProfileSelectInputConnector
        {...shared}
        name="metadata"
        value={this.state.metadata}
      />
    </>);
  }
}

ProfileContractFixture.propTypes = {
  quality: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  metadata: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  includeNone: PropTypes.bool,
  isDisabled: PropTypes.bool
};

window.renderProfileContractFixture = (props) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const appStore = createAppStore(history);
  window.profileChanges = [];
  loadProfiles(appStore);
  ReactDOM.render(<Provider store={appStore}><ProfileContractFixture {...props} /></Provider>, root);
};
