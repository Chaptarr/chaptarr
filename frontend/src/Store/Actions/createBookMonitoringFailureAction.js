import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import { showMessage } from './appActions';

let messageSequence = 0;

export default function createBookMonitoringFailureAction(xhr) {
  messageSequence += 1;

  return showMessage({
    id: `book-monitoring-update-failed-${Date.now()}-${messageSequence}`,
    name: 'BookMonitoringUpdateFailed',
    message: getErrorMessage(xhr, translate('UnableToUpdateBookMonitoring')),
    type: 'error',
    hideAfter: 10
  });
}
