import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { pauseCommand, resumeCommand } from 'Store/Actions/commandActions';
import ImportProgressIndicator from './ImportProgressIndicator';

function createMapStateToProps() {
  return createSelector(
    (state) => state.app.importTracker,
    (state) => state.commands.items,
    (importTracker, commands) => {
      const activeImportCommand = commands && commands.find((cmd) =>
        cmd.name === 'RescanFolders' &&
        (cmd.status === 'started' || cmd.status === 'paused')
      );

      const isActive = Boolean(importTracker?.isActive || activeImportCommand);
      const isPaused = Boolean(activeImportCommand?.status === 'paused');

      return {
        importTracker,
        isActive,
        isPaused,
        commandId: activeImportCommand?.id || null
      };
    }
  );
}

const mapDispatchToProps = {
  pauseCommand,
  resumeCommand
};

function mergeProps(stateProps, dispatchProps, ownProps) {
  const { commandId, isPaused } = stateProps;

  return {
    ...ownProps,
    ...stateProps,
    onPauseResume: commandId ? () => {
      if (isPaused) {
        dispatchProps.resumeCommand({ id: commandId });
      } else {
        dispatchProps.pauseCommand({ id: commandId });
      }
    } : null
  };
}

export default connect(createMapStateToProps, mapDispatchToProps, mergeProps)(ImportProgressIndicator);
