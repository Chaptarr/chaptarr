import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './HistoryEventTypeCell.css';

function getIconName(eventType) {
  switch (eventType) {
    case 'grabbed':
      return icons.DOWNLOADING;
    case 'authorFolderImported':
      return icons.DRIVE;
    case 'bookFileImported':
      return icons.DOWNLOADED;
    case 'bookFileConverted':
      return icons.REFRESH;
    case 'bookFileConversionFailed':
      return icons.REFRESH;
    case 'downloadFailed':
      return icons.DOWNLOADING;
    case 'bookFileDeleted':
      return icons.DELETE;
    case 'bookFileRenamed':
      return icons.ORGANIZE;
    case 'bookFileRetagged':
      return icons.RETAG;
    case 'bookImportIncomplete':
      return icons.DOWNLOADED;
    case 'downloadIgnored':
      return icons.IGNORE;
    default:
      return icons.UNKNOWN;
  }
}

function getIconKind(eventType) {
  switch (eventType) {
    case 'downloadFailed':
      return kinds.DANGER;
    case 'bookFileConverted':
      return kinds.SUCCESS;
    case 'bookFileConversionFailed':
      return kinds.DANGER;
    case 'bookImportIncomplete':
      return kinds.WARNING;
    default:
      return kinds.DEFAULT;
  }
}

function getTooltip(eventType, data) {
  switch (eventType) {
    case 'grabbed':
      return translate('HistoryGrabbedTooltip', {
        indexer: data.indexer,
        downloadClient: data.downloadClient
      });
    case 'authorFolderImported':
      return translate('HistoryImportedFromAuthorFolderTooltip');
    case 'bookFileImported':
      return data && (data.downloadClient || data.downloadClientName)
        ? translate('HistoryDownloadedFromClientTooltip')
        : translate('BookImported');
    case 'bookFileConverted':
      return data && data.importedPath
        ? translate('HistoryConvertedBeforeImportTooltip')
        : translate('HistoryConvertedImportIncompleteTooltip');
    case 'bookFileConversionFailed':
      return translate('HistoryBookConversionFailedTitle');
    case 'downloadFailed':
      return translate('DownloadFailed');
    case 'bookFileDeleted':
      return translate('HistoryBookFileDeletedTooltip');
    case 'bookFileRenamed':
      return translate('HistoryBookFileRenamedTooltip');
    case 'bookFileRetagged':
      return translate('HistoryBookFileTagsUpdatedTooltip');
    case 'bookImportIncomplete':
      return translate('HistoryFilesImportIncompleteTooltip');
    case 'downloadIgnored':
      return translate('HistoryDownloadIgnoredTitle');
    default:
      return translate('HistoryUnknownEventTooltip');
  }
}

function HistoryEventTypeCell({ eventType, data }) {
  const iconName = getIconName(eventType);
  const iconKind = getIconKind(eventType);
  const tooltip = getTooltip(eventType, data);

  return (
    <TableRowCell
      className={styles.cell}
      title={tooltip}
    >
      <Icon
        name={iconName}
        kind={iconKind}
      />
    </TableRowCell>
  );
}

HistoryEventTypeCell.propTypes = {
  eventType: PropTypes.string.isRequired,
  data: PropTypes.object
};

HistoryEventTypeCell.defaultProps = {
  data: {}
};

export default HistoryEventTypeCell;
