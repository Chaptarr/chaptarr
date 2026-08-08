import React from 'react';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';

function BookMonitoringOptionsPopoverContent() {
  return (
    <DescriptionList>
      <DescriptionListItem
        title="All books"
        data="Monitor all existing and future books from this author"
      />

      <DescriptionListItem
        title="None (Just this one)"
        data="Only monitor this specific book, not other books from the author"
      />
    </DescriptionList>
  );
}

export default BookMonitoringOptionsPopoverContent;