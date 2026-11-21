function isCommandComplete(command) {
  if (!command) {
    return false;
  }

  return command.status === 'completed';
}

export default isCommandComplete;
