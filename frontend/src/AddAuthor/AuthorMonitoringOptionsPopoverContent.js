import React from 'react';
import Alert from 'Components/Alert';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import translate from 'Utilities/String/translate';

function AuthorMonitoringOptionsPopoverContent() {
  return (
    <>
      <Alert>
        {translate('MonitoringOptionsHelpText')}
      </Alert>
      <DescriptionList>
        <DescriptionListItem
          title={translate('AllBooks')}
          data={translate('DataAllBooks')}
        />

        <DescriptionListItem
          title={translate('MissingBooks')}
          data={translate('DataMissingBooks')}
        />

        <DescriptionListItem
          title={translate('BooksWithFiles')}
          data={translate('DataExistingBooks')}
        />

        <DescriptionListItem
          title={translate('None')}
          data={translate('DataNone')}
        />
      </DescriptionList>
    </>
  );
}

export default AuthorMonitoringOptionsPopoverContent;
