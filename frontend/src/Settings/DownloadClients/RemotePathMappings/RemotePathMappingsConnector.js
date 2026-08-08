import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { deleteRemotePathMapping, fetchDownloadClients, fetchRemotePathMappings } from 'Store/Actions/settingsActions';
import RemotePathMappings from './RemotePathMappings';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.remotePathMappings,
    (state) => state.settings.downloadClients,
    (remotePathMappings, downloadClients) => {
      const downloadClientsById = downloadClients.items.reduce((acc, downloadClient) => {
        acc[downloadClient.id] = downloadClient;
        return acc;
      }, {});

      return {
        ...remotePathMappings,
        items: remotePathMappings.items.map((item) => {
          const downloadClient = downloadClientsById[item.downloadClientId];

          return {
            ...item,
            downloadClientName: downloadClient?.name
          };
        }),
        downloadClientsPopulated: downloadClients.isPopulated
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchRemotePathMappings: fetchRemotePathMappings,
  dispatchFetchDownloadClients: fetchDownloadClients,
  dispatchDeleteRemotePathMapping: deleteRemotePathMapping
};

class RemotePathMappingsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchRemotePathMappings();

    if (!this.props.downloadClientsPopulated) {
      this.props.dispatchFetchDownloadClients();
    }
  }

  //
  // Listeners

  onConfirmDeleteRemotePathMapping = (id) => {
    this.props.dispatchDeleteRemotePathMapping({ id });
  };

  //
  // Render

  render() {
    return (
      <RemotePathMappings
        {...this.state}
        {...this.props}
        onConfirmDeleteRemotePathMapping={this.onConfirmDeleteRemotePathMapping}
      />
    );
  }
}

RemotePathMappingsConnector.propTypes = {
  dispatchFetchRemotePathMappings: PropTypes.func.isRequired,
  dispatchFetchDownloadClients: PropTypes.func.isRequired,
  downloadClientsPopulated: PropTypes.bool.isRequired,
  dispatchDeleteRemotePathMapping: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(RemotePathMappingsConnector);
