import React from 'react';
import Alert from 'Components/Alert';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import translate from 'Utilities/String/translate';

function BookMonitoringOptionsPopoverContent() {
  return (
    <>
      <Alert>
        {translate('BookRequestMonitoringOptionsHelpText')}
      </Alert>

      <DescriptionList>
        <DescriptionListItem
          title={translate('AllBooks')}
          data={translate('DataAllBooksForBookRequest')}
        />

        <DescriptionListItem
          title={translate('OnlyThisBook')}
          data={translate('DataOnlyThisBook')}
        />
      </DescriptionList>
    </>
  );
}

export default BookMonitoringOptionsPopoverContent;
