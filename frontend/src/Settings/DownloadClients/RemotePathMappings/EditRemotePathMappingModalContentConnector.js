import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { saveRemotePathMapping, setRemotePathMappingValue } from 'Store/Actions/settingsActions';
import selectSettings from 'Store/Selectors/selectSettings';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import EditRemotePathMappingModalContent from './EditRemotePathMappingModalContent';

const newRemotePathMapping = {
  downloadClientId: 0,
  host: '',
  remotePath: '',
  localPath: ''
};

const MAX_PATH_SUGGESTIONS = 6;

function limitPathSuggestions(suggestions) {
  return (suggestions || [])
    .filter((suggestion) => suggestion)
    .slice(0, MAX_PATH_SUGGESTIONS);
}

function getDownloadClientHost(downloadClient) {
  const host = downloadClient.fields.find((field) => {
    return field.name === 'host';
  });

  return host?.value || '';
}

const selectRemotePathMappingOptions = createSelector(
  (state) => state.settings.downloadClients.items,
  (state) => state.settings.rootFolders.items,
  (downloadClients, rootFolders) => {
    const dlhosts = downloadClients.reduce((acc, downloadClient) => {
      const name = downloadClient.name;
      const host = getDownloadClientHost(downloadClient);

      if (host) {
        const group = acc[host] = acc[host] || [];
        group.push(name);
      }

      return acc;
    }, {});

    const hosts = rootFolders.reduce((acc, folder) => {
      const name = folder.name;

      if (folder.isCalibreLibrary && folder.host) {
        const group = acc[folder.host] = acc[folder.host] || [];
        group.push(name);
      }

      return acc;
    }, dlhosts);

    const downloadClientHosts = Object.keys(hosts).map((host) => {
      return {
        key: host,
        value: host,
        hint: `${hosts[host].join(', ')}`
      };
    });

    const downloadClientOptions = downloadClients.map((downloadClient) => {
      const host = getDownloadClientHost(downloadClient);

      return {
        key: downloadClient.id,
        value: downloadClient.name,
        hint: !host ? 'No host configured' : host,
        isDisabled: !host,
        host
      };
    });

    downloadClientOptions.unshift({
      key: 0,
      value: 'Host-wide mapping',
      hint: 'Traditional mapping by host'
    });

    return {
      downloadClientHosts,
      downloadClientOptions
    };
  }
);

function createRemotePathMappingSelector() {
  return createSelector(
    (state, { id }) => id,
    (state) => state.settings.remotePathMappings,
    selectRemotePathMappingOptions,
    (id, remotePathMappings, remotePathMappingOptions) => {
      const {
        isFetching,
        error,
        isSaving,
        saveError,
        pendingChanges,
        items
      } = remotePathMappings;

      const mapping = id ? _.find(items, { id }) : newRemotePathMapping;
      const settings = selectSettings(mapping, pendingChanges, saveError);

      return {
        id,
        isFetching,
        error,
        isSaving,
        saveError,
        item: settings.settings,
        ...settings,
        ...remotePathMappingOptions
      };
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    createRemotePathMappingSelector(),
    (remotePathMapping) => {
      return {
        ...remotePathMapping
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchSetRemotePathMappingValue: setRemotePathMappingValue,
  dispatchSaveRemotePathMapping: saveRemotePathMapping
};

class EditRemotePathMappingModalContentConnector extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      isTesting: false,
      testError: null,
      testResult: null,
      downloadClientPathSuggestions: [],
      chaptarrPathSuggestions: [],
      showAdvancedScope: props.item.downloadClientId.value > 0
    };
  }

  //
  // Lifecycle

  componentDidMount() {
    if (!this.props.id) {
      Object.keys(newRemotePathMapping).forEach((name) => {
        this.props.dispatchSetRemotePathMappingValue({
          name,
          value: newRemotePathMapping[name]
        });
      });
    }

    this.fetchSuggestions(this.props.item.downloadClientId.value, this.props.item.host.value);
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }

    const prevDownloadClientId = prevProps.item.downloadClientId.value;
    const downloadClientId = this.props.item.downloadClientId.value;
    const prevHost = prevProps.item.host.value;
    const host = this.props.item.host.value;

    if (downloadClientId > 0 && !this.state.showAdvancedScope) {
      this.setState({ showAdvancedScope: true });
    }

    if (prevDownloadClientId !== downloadClientId || prevHost !== host) {
      this.fetchSuggestions(downloadClientId, host);
    }
  }

  componentWillUnmount() {
    if (this._testRequest) {
      this._testRequest.abortRequest();
    }

    if (this._suggestionsRequest) {
      this._suggestionsRequest.abortRequest();
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({
      testError: null,
      testResult: null
    });

    if (name === 'downloadClientId') {
      const downloadClientId = parseInt(value);

      this.props.dispatchSetRemotePathMappingValue({ name, value: downloadClientId });

      const selectedClient = this.props.downloadClientOptions.find((option) => {
        return option.key === downloadClientId;
      });

      this.props.dispatchSetRemotePathMappingValue({ name: 'host', value: selectedClient?.host || '' });

      return;
    }

    this.props.dispatchSetRemotePathMappingValue({ name, value });
  };

  onSavePress = () => {
    this.props.dispatchSaveRemotePathMapping({ id: this.props.id });
  };

  onAdvancedScopePress = () => {
    if (this.props.item.downloadClientId.value > 0) {
      return;
    }

    this.setState((state) => {
      return {
        showAdvancedScope: !state.showAdvancedScope
      };
    });
  };

  onTestPress = () => {
    if (this._testRequest) {
      this._testRequest.abortRequest();
    }

    const {
      downloadClientId,
      host,
      remotePath,
      localPath
    } = this.props.item;

    this.setState({
      isTesting: true,
      testError: null,
      testResult: null
    });

    this._testRequest = createAjaxRequest({
      url: '/remotepathmapping/test',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({
        downloadClientId: downloadClientId.value,
        host: host.value,
        remotePath: remotePath.value,
        localPath: localPath.value
      })
    });

    this._testRequest.request.done((result) => {
      this.setState({
        isTesting: false,
        testError: null,
        testResult: result
      });
    });

    this._testRequest.request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this.setState({
        isTesting: false,
        testError: xhr,
        testResult: null
      });
    });
  };

  fetchSuggestions = (downloadClientId = 0, host = '') => {
    if (this._suggestionsRequest) {
      this._suggestionsRequest.abortRequest();
    }

    this._suggestionsRequest = createAjaxRequest({
      url: '/remotepathmapping/suggestions',
      method: 'GET',
      dataType: 'json',
      data: {
        downloadClientId,
        host
      }
    });

    this._suggestionsRequest.request.done((result) => {
      this.setState({
        downloadClientPathSuggestions: limitPathSuggestions(result.downloadClientPaths),
        chaptarrPathSuggestions: limitPathSuggestions(result.chaptarrPaths)
      });
    });

    this._suggestionsRequest.request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this.setState({
        downloadClientPathSuggestions: [],
        chaptarrPathSuggestions: []
      });
    });
  };

  //
  // Render

  render() {
    return (
      <EditRemotePathMappingModalContent
        {...this.props}
        {...this.state}
        onSavePress={this.onSavePress}
        onAdvancedScopePress={this.onAdvancedScopePress}
        onTestPress={this.onTestPress}
        onInputChange={this.onInputChange}
      />
    );
  }
}

EditRemotePathMappingModalContentConnector.propTypes = {
  id: PropTypes.number,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  downloadClientOptions: PropTypes.arrayOf(PropTypes.object).isRequired,
  dispatchSetRemotePathMappingValue: PropTypes.func.isRequired,
  dispatchSaveRemotePathMapping: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditRemotePathMappingModalContentConnector);
