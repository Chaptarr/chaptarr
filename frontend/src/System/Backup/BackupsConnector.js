import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { cancelRestore, deleteBackup, dismissFailedRestores, fetchBackups, fetchBackupStatus } from 'Store/Actions/systemActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import Backups from './Backups';

function createMapStateToProps() {
  return createSelector(
    (state) => state.system.backups,
    createCommandExecutingSelector(commandNames.BACKUP),
    (state) => state.system.backupStatus,
    (backups, backupExecuting, backupStatus) => {
      const {
        isFetching,
        isPopulated,
        error,
        items
      } = backups;

      return {
        isFetching,
        isPopulated,
        error,
        items,
        backupExecuting,
        backupStatus: backupStatus.item,
        backupStatusError: backupStatus.error,
        restoreError: backups.restoreError
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    dispatchFetchBackups() {
      dispatch(fetchBackups());
      dispatch(fetchBackupStatus());
    },

    onDeleteBackupPress(id) {
      dispatch(deleteBackup({ id }));
    },

    onDismissFailedRestoresPress() {
      dispatch(dismissFailedRestores());
    },

    onCancelRestorePress() {
      dispatch(cancelRestore());
    },

    onBackupPress() {
      dispatch(executeCommand({
        name: commandNames.BACKUP
      }));
    }
  };
}

class BackupsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchBackups();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.backupExecuting && !this.props.backupExecuting) {
      this.props.dispatchFetchBackups();
    }
  }

  //
  // Render

  render() {
    return (
      <Backups
        {...this.props}
      />
    );
  }
}

BackupsConnector.propTypes = {
  backupExecuting: PropTypes.bool.isRequired,
  dispatchFetchBackups: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, createMapDispatchToProps)(BackupsConnector);
