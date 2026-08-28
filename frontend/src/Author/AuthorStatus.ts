import { AuthorStatus } from 'Author/Author';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

export function getAuthorStatusDetails(status: AuthorStatus) {
  let statusDetails = {
    icon: icons.AUTHOR_CONTINUING,
    title: translate('Active'),
    message: translate('AuthorActiveStatusMessage'),
  };

  if (status === 'ended') {
    statusDetails = {
      icon: icons.AUTHOR_ENDED,
      title: translate('Dead'),
      message: translate('AuthorDeadStatusMessage'),
    };
  }

  return statusDetails;
}
