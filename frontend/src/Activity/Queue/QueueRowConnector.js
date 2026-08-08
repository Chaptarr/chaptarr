import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { cancelQueueConversion, grabQueueItem, removeQueueItem } from 'Store/Actions/queueActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import QueueRow from './QueueRow';

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    createBookSelector(),
    (state, props) => props.author,
    (state, props) => props.book,
    (state) => state.commands.items,
    (state, props) => props.downloadId,
    createUISettingsSelector(),
    (authorFromStore, bookFromStore, authorFromItem, bookFromItem, commands, downloadId, uiSettings) => {
      const result = _.pick(uiSettings, [
        'showRelativeDates',
        'shortDateFormat',
        'timeFormat'
      ]);

      result.author = authorFromStore ?? authorFromItem;
      result.book = bookFromStore ?? bookFromItem;
      const retryCommand = downloadId ?
        findCommand(commands, { name: commandNames.RETRY_FAILED_IMPORT, downloadId }) ||
          commands.find((command) => {
            const downloadIds = command.body?.downloadIds;
            const commandName = command.body?.name || command.name;

            return commandName === commandNames.RETRY_FAILED_IMPORT &&
              Array.isArray(downloadIds) &&
              downloadIds.includes(downloadId) &&
              isCommandExecuting(command);
          }) :
        null;

      result.isRetryingImport = isCommandExecuting(retryCommand);

      return result;
    }
  );
}

const mapDispatchToProps = {
  executeCommand,
  cancelQueueConversion,
  grabQueueItem,
  removeQueueItem
};

class QueueRowConnector extends Component {

  //
  // Listeners

  onGrabPress = () => {
    this.props.grabQueueItem({ id: this.props.id });
  };

  onRetryImportPress = () => {
    this.props.executeCommand({
      name: commandNames.RETRY_FAILED_IMPORT,
      downloadId: this.props.downloadId
    });
  };

  onCancelConversionPress = () => {
    this.props.cancelQueueConversion({ id: this.props.id });
  };

  onRemoveQueueItemPress = (payload) => {
    this.props.removeQueueItem({ id: this.props.id, ...payload });
  };

  //
  // Render

  render() {
    return (
      <QueueRow
        {...this.props}
        onGrabPress={this.onGrabPress}
        onRetryImportPress={this.onRetryImportPress}
        onCancelConversionPress={this.onCancelConversionPress}
        onRemoveQueueItemPress={this.onRemoveQueueItemPress}
      />
    );
  }
}

QueueRowConnector.propTypes = {
  id: PropTypes.number.isRequired,
  downloadId: PropTypes.string,
  author: PropTypes.object,
  book: PropTypes.object,
  executeCommand: PropTypes.func.isRequired,
  cancelQueueConversion: PropTypes.func.isRequired,
  grabQueueItem: PropTypes.func.isRequired,
  removeQueueItem: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(QueueRowConnector);
