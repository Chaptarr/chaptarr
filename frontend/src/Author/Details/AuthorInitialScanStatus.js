import PropTypes from 'prop-types';
import React from 'react';
import { connect } from 'react-redux';
import * as commandNames from 'Commands/commandNames';
import Alert from 'Components/Alert';
import SpinnerButton from 'Components/Link/SpinnerButton';
import { kinds, sizes } from 'Helpers/Props';
import { executeCommand } from 'Store/Actions/commandActions';
import { isCommandExecuting } from 'Utilities/Command';
import translate from 'Utilities/String/translate';

export function AuthorInitialScanStatus({ authorId, paths, isScanning, executeCommand: runCommand }) {
  if (!paths?.length) {
    return null;
  }
  return (
    <Alert kind={kinds.INFO}>
      {translate('InitialSetupWaitingForScan', { folder: paths.join(', ') })}
      {' '}
      <SpinnerButton size={sizes.SMALL} isSpinning={isScanning}
        isDisabled={isScanning}
        onPress={() => runCommand({ name: commandNames.RESCAN_FOLDERS, authorIds: [authorId], folders: paths, filter: 'matched' })}
      >
        {translate('RescanNow')}
      </SpinnerButton>
    </Alert>
  );
}

AuthorInitialScanStatus.propTypes = {
  authorId: PropTypes.number.isRequired,
  paths: PropTypes.arrayOf(PropTypes.string),
  isScanning: PropTypes.bool.isRequired,
  executeCommand: PropTypes.func.isRequired
};

export default connect((state, { authorId }) => ({
  isScanning: (state.commands.items || []).some((command) =>
    command.name === commandNames.RESCAN_FOLDERS && isCommandExecuting(command) &&
    (!command.body?.authorIds?.length || command.body.authorIds.includes(authorId)))
}), { executeCommand })(AuthorInitialScanStatus);
