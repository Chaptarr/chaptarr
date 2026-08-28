import React from 'react';
import Alert from 'Components/Alert';
import translate from 'Utilities/String/translate';

function AuthorMonitoringGatePopoverContent() {
  return (
    <Alert>
      {translate('AuthorMonitoringGateDetailedHelpText')}
    </Alert>
  );
}

export default AuthorMonitoringGatePopoverContent;
