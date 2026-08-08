import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { testAllDownloadClients, testAllIndexers } from 'Store/Actions/settingsActions';
import { fetchHealth } from 'Store/Actions/systemActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createHealthCheckSelector from 'Store/Selectors/createHealthCheckSelector';
import Health from './Health';

function createMapStateToProps() {
  return createSelector(
    createHealthCheckSelector(),
    (state) => state.system.health,
    (state) => state.settings.downloadClients.isTestingAll,
    (state) => state.settings.indexers.isTestingAll,
    createCommandExecutingSelector(commandNames.CHECK_HEALTH),
    (items, health, isTestingAllDownloadClients, isTestingAllIndexers, isRunningHealthCheck) => {
      const {
        isFetching,
        isPopulated
      } = health;

      return {
        isFetching,
        isPopulated,
        items,
        isTestingAllDownloadClients,
        isTestingAllIndexers,
        isRunningHealthCheck
      };
    }
  );
}

function createMapDispatchToProps(dispatch) {
  return {
    dispatchFetchHealth() {
      dispatch(fetchHealth());
    },
    dispatchTestAllDownloadClients() {
      dispatch(testAllDownloadClients());
    },
    dispatchTestAllIndexers() {
      dispatch(testAllIndexers());
    },
    dispatchRunHealthCheck() {
      dispatch(executeCommand({ name: commandNames.CHECK_HEALTH }));
    }
  };
}

class HealthConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchHealth();
  }

  //
  // Render

  render() {
    const {
      dispatchFetchHealth,
      ...otherProps
    } = this.props;

    return (
      <Health
        {...otherProps}
      />
    );
  }
}

HealthConnector.propTypes = {
  dispatchFetchHealth: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, createMapDispatchToProps)(HealthConnector);
