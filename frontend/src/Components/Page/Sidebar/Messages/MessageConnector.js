import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { hideMessage } from 'Store/Actions/appActions';
import { pauseCommand, resumeCommand } from 'Store/Actions/commandActions';
import Message from './Message';

function createMapStateToProps() {
  return createSelector(
    (state) => state.commands.items,
    (state, props) => props.id,
    (state, props) => props.commandId,
    (state, props) => props.type,
    (commands, messageId, relatedCommandId, messageType) => {
      let commandStatus = null;

      const commandLookupId = relatedCommandId || messageId;
      const directCommand = commands.find((cmd) => cmd.id === commandLookupId);
      if (directCommand) {
        commandStatus = directCommand.status;
      }

      // For import progress messages, prefer the active RescanFolders command.
      if (messageType === 'importProgress') {
        const rescanCommand = commands.find(cmd => 
          cmd.name === 'RescanFolders' && 
          (cmd.status === 'started' || cmd.status === 'paused')
        );
        commandStatus = rescanCommand ? rescanCommand.status : commandStatus;
      }
      
      return {
        commands,  // Pass all commands so we can find RescanFolders
        commandStatus
      };
    }
  );
}

const mapDispatchToProps = {
  hideMessage,
  pauseCommand,
  resumeCommand
};

class MessageConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._hideTimeoutId = null;
    // Initialize pause state based on command status for import progress messages
    const initialPaused = props.type === 'importProgress' && props.commandStatus === 'paused';
    this.state = {
      isPaused: initialPaused
    };
    this.scheduleHideMessage(props.hideAfter);
  }

  componentDidUpdate(prevProps) {
    this.scheduleHideMessage(this.props.hideAfter);
    
    // Sync pause state with command status
    if (this.props.type === 'importProgress' && this.props.commandStatus !== prevProps.commandStatus) {
      const isPaused = this.props.commandStatus === 'paused';
      if (isPaused !== this.state.isPaused) {
        this.setState({ isPaused });
      }
    }
  }

  //
  // Control

  scheduleHideMessage = (hideAfter) => {
    if (this._hideTimeoutId) {
      clearTimeout(this._hideTimeoutId);
    }

    // Don't auto-hide if hideAfter is 0 (used for persistent messages like paused imports)
    if (hideAfter && hideAfter > 0) {
      this._hideTimeoutId = setTimeout(this.hideMessage, hideAfter * 1000);
    }
  };

  hideMessage = () => {
    this.props.hideMessage({ id: this.props.id });
  };
  
  handlePauseResume = () => {
    const { type, id } = this.props;
    
    // Only handle pause/resume for import progress messages
    if (type !== 'importProgress') {
      console.warn('[MessageConnector] Pause/resume called on non-import message');
      return;
    }
    
    // Find the active RescanFolders command
    const { commands } = this.props;
    const activeImportCommand = commands && commands.find(cmd => 
      cmd.name === 'RescanFolders' && 
      (cmd.status === 'started' || cmd.status === 'paused')
    );
    
    if (!activeImportCommand) {
      console.warn('[MessageConnector] Cannot pause/resume - no active RescanFolders command found');
      return;
    }
    
    const commandId = activeImportCommand.id;
    const newPausedState = !this.state.isPaused;
    this.setState({ isPaused: newPausedState });
    
    console.log('[MessageConnector] Import', newPausedState ? 'pausing' : 'resuming', 'command:', commandId);
    
    // Dispatch pause/resume command
    if (newPausedState) {
      this.props.pauseCommand({ id: commandId });
    } else {
      this.props.resumeCommand({ id: commandId });
    }
  };

  //
  // Render

  render() {
    const { type } = this.props;
    const extraProps = {};
    
    // Add pause handler for import progress messages
    if (type === 'importProgress') {
      extraProps.onPauseResume = this.handlePauseResume;
      extraProps.isPaused = this.state.isPaused;
    }
    
    return (
      <Message
        {...this.props}
        {...extraProps}
      />
    );
  }
}

MessageConnector.propTypes = {
  id: PropTypes.oneOfType([PropTypes.number, PropTypes.string]).isRequired,
  commandId: PropTypes.number,
  hideAfter: PropTypes.number.isRequired,
  hideMessage: PropTypes.func.isRequired,
  pauseCommand: PropTypes.func.isRequired,
  resumeCommand: PropTypes.func.isRequired,
  type: PropTypes.string,
  commands: PropTypes.array
};

MessageConnector.defaultProps = {
  // Hide messages after 60 seconds if there is no activity
  hideAfter: 60
};

export default connect(createMapStateToProps, mapDispatchToProps)(MessageConnector);
