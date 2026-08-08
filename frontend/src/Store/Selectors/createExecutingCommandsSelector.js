import { createSelector } from 'reselect';
import { isCommandExecuting } from 'Utilities/Command';

function createExecutingCommandsSelector() {
  return createSelector(
    (state) => state.commands.items,
    (commands) => {
      // Ensure commands is an array before filtering
      if (!commands || !Array.isArray(commands)) {
        return [];
      }
      return commands.filter((command) => isCommandExecuting(command));
    }
  );
}

export default createExecutingCommandsSelector;
