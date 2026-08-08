import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './QueueStatusCell.css';

function getDetailedPopoverBody(statusMessages) {
  return (
    <div>
      {
        statusMessages.map(({ title, messages }) => {
          return (
            <div key={title}>
              {title}
              <ul>
                {
                  messages.map((message) => {
                    return (
                      <li key={message}>
                        {message}
                      </li>
                    );
                  })
                }
              </ul>
            </div>
          );
        })
      }
    </div>
  );
}

function QueueStatusCell(props) {
  const {
    sourceTitle,
    status,
    trackedDownloadStatus,
    trackedDownloadState,
    statusMessages,
    errorMessage,
    conversionStatus,
    conversionMessage,
    convertToQuality
  } = props;

  const hasWarning = trackedDownloadStatus === 'warning';
  const hasError = trackedDownloadStatus === 'error';
  const isConverting = conversionStatus === 'converting' || conversionStatus === 'cancelling';
  const isConversionQueued = conversionStatus === 'queued';
  const isReadyToImport = conversionStatus === 'ready_to_import';
  const isConversionFailed = conversionStatus === 'failed';
  const isConversionCancelled = conversionStatus === 'cancelled';
  const isConversionTerminal = isConversionFailed || isConversionCancelled;
  const isWaitingForConversion = !isConverting && !isConversionQueued && !isReadyToImport && !isConversionFailed && !isConversionCancelled && trackedDownloadState === 'importPending' && !!convertToQuality;
  const isPreparingConversion = !isConverting && !isConversionTerminal && trackedDownloadState === 'importing' && !!convertToQuality;
  const showTrackedDownloadMessages = !isConverting && !isConversionTerminal && (hasWarning || hasError);
  let popoverBody = sourceTitle;

  if (showTrackedDownloadMessages) {
    popoverBody = getDetailedPopoverBody(statusMessages);
  }

  if (isConversionFailed && conversionMessage) {
    popoverBody = conversionMessage;
  }

  if (isConversionCancelled && conversionMessage) {
    popoverBody = conversionMessage;
  }

  // status === 'downloading'
  let iconName = icons.DOWNLOADING;
  let iconKind = kinds.DEFAULT;
  let title = translate('Title');

  if (status === 'paused') {
    iconName = icons.PAUSED;
    title = 'Paused';
  }

  if (status === 'queued') {
    iconName = icons.QUEUED;
    title = 'Queued';
  }

  if (status === 'completed') {
    iconName = icons.DOWNLOADED;
    title = 'Downloaded';

    if (isConverting) {
      iconName = icons.REFRESH;
      title = conversionStatus === 'cancelling' ? 'Cancelling conversion' : `Converting${convertToQuality ? ` to ${convertToQuality}` : ''}`;
      iconKind = kinds.PRIMARY;
    }

    if (isConversionQueued) {
      iconName = icons.QUEUED;
      title = convertToQuality ?
        translate('ConversionQueuedToQuality', { quality: convertToQuality }) :
        translate('ConversionQueued');
      iconKind = kinds.PURPLE;
    }

    if (isReadyToImport) {
      iconName = icons.DOWNLOADED;
      title = translate('ConvertedWaitingToImport');
      iconKind = kinds.PURPLE;
    }

    if (isWaitingForConversion) {
      iconName = icons.REFRESH;
      title = `Downloaded - Waiting to Convert to ${convertToQuality}`;
      iconKind = kinds.PURPLE;
    }

    if (isPreparingConversion) {
      iconName = icons.REFRESH;
      title = `Preparing conversion to ${convertToQuality}`;
      iconKind = kinds.PRIMARY;
    }

    if (!isConverting && !isWaitingForConversion && !isPreparingConversion && trackedDownloadState === 'importPending') {
      title += ' - Waiting to Import';
      iconKind = kinds.PURPLE;
    }

    if (!isConverting && !isPreparingConversion && trackedDownloadState === 'importing') {
      title += ' - Importing';
      iconKind = kinds.PRIMARY;
    }

    if (trackedDownloadState === 'importBlocked') {
      title = `${translate('Downloaded')} - ${translate('ManualImport')} ${translate('Required')}`;
      iconKind = kinds.WARNING;
    }

    if (trackedDownloadState === 'failedPending') {
      title += ' - Waiting to Process';
      iconKind = kinds.DANGER;
    }
  }

  if (hasWarning && !isConverting && !isConversionFailed && !isConversionCancelled) {
    iconKind = kinds.WARNING;
  }

  if (isConversionFailed) {
    iconName = icons.DANGER;
    iconKind = kinds.DANGER;
    title = 'Conversion failed';
  }

  if (isConversionCancelled) {
    iconName = icons.STOP;
    iconKind = kinds.WARNING;
    title = 'Conversion cancelled';
  }

  if (status === 'delay') {
    iconName = icons.PENDING;
    title = 'Pending';
  }

  if (status === 'downloadClientUnavailable') {
    iconName = icons.PENDING;
    iconKind = kinds.WARNING;
    title = 'Pending - Download client is unavailable';
  }

  if (status === 'failed') {
    iconName = icons.DOWNLOADING;
    iconKind = kinds.DANGER;
    title = 'Download failed';
  }

  if (status === 'warning') {
    iconName = icons.DOWNLOADING;
    iconKind = kinds.WARNING;
    title = `Download warning: ${errorMessage || 'check download client for more details'}`;
  }

  if (hasError && !isConverting && !isConversionFailed && !isConversionCancelled) {
    if (status === 'completed') {
      iconName = icons.DOWNLOAD;
      iconKind = kinds.DANGER;
      title = `Import failed: ${sourceTitle}`;
    } else {
      iconName = icons.DOWNLOADING;
      iconKind = kinds.DANGER;
      title = 'Download failed';
    }
  }

  return (
    <TableRowCell className={styles.status}>
      <Popover
        anchor={
          <Icon
            name={iconName}
            kind={iconKind}
            className={isConverting ? styles.convertingIcon : null}
          />
        }
        title={title}
        body={popoverBody}
        position={tooltipPositions.RIGHT}
        canFlip={false}
      />
    </TableRowCell>
  );
}

QueueStatusCell.propTypes = {
  sourceTitle: PropTypes.string.isRequired,
  status: PropTypes.string.isRequired,
  trackedDownloadStatus: PropTypes.string.isRequired,
  trackedDownloadState: PropTypes.string.isRequired,
  statusMessages: PropTypes.arrayOf(PropTypes.object),
  errorMessage: PropTypes.string,
  conversionStatus: PropTypes.string,
  conversionMessage: PropTypes.string,
  convertToQuality: PropTypes.string
};

QueueStatusCell.defaultProps = {
  trackedDownloadStatus: 'Ok',
  trackedDownloadState: 'Downloading'
};

export default QueueStatusCell;
