import React from 'react';
import Alert from 'Components/Alert';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import translate from 'Utilities/String/translate';

function AuthorMonitorNewItemsOptionsPopoverContent() {
  return (
    <>
      <Alert>
        {translate('MonitorNewItemsHelpText')}
      </Alert>

      <DescriptionList>
        <DescriptionListItem
          title={translate('AllNewBooks')}
          data={translate('DataNewAllBooks')}
        />

        <DescriptionListItem
          title={translate('FutureReleases')}
          data={translate('DataNewBooks')}
        />

        <DescriptionListItem
          title={translate('None')}
          data={translate('DataNewNone')}
        />
      </DescriptionList>
    </>
  );
}

export default AuthorMonitorNewItemsOptionsPopoverContent;
