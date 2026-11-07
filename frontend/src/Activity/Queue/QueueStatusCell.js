import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';
import styles from './QueueStatusCell.css';

function getDetailedPopoverBody(statusMessages) {
  return (
    <div>
      {
        statusMessages.map(({ title, messages }) => {
          return (
            <div key={title}>
              {translateServerMessage(title)}
              <ul>
                {
                  messages.map((message) => {
                    return (
                      <li key={message}>
                        {translateServerMessage(message)}
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
    popoverBody = translateServerMessage(conversionMessage);
  }

  if (isConversionCancelled && conversionMessage) {
    popoverBody = translateServerMessage(conversionMessage);
  }

  // status === 'downloading'
  let iconName = icons.DOWNLOADING;
  let iconKind = kinds.DEFAULT;
  let title = translate('QueueStatusDownloading');

  if (status === 'paused') {
    iconName = icons.PAUSED;
    title = translate('QueueStatusPaused');
  }

  if (status === 'queued') {
    iconName = icons.QUEUED;
    title = translate('Queued');
  }

  if (status === 'completed') {
    iconName = icons.DOWNLOADED;
    title = translate('Downloaded');

    if (isConverting) {
      iconName = icons.REFRESH;
      if (conversionStatus === 'cancelling') {
        title = translate('CancellingConversion');
      } else if (convertToQuality) {
        title = translate('ConvertingToQuality', { quality: convertToQuality });
      } else {
        title = translate('Converting');
      }
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
      title = translate('WaitingToConvertToQuality', { quality: convertToQuality });
      iconKind = kinds.PURPLE;
    }

    if (isPreparingConversion) {
      iconName = icons.REFRESH;
      title = translate('PreparingConversionToQuality', { quality: convertToQuality });
      iconKind = kinds.PRIMARY;
    }

    if (!isConverting && !isWaitingForConversion && !isPreparingConversion && trackedDownloadState === 'importPending') {
      title = translate('QueueStatusDownloadedWaitingToImport');
      iconKind = kinds.PURPLE;
    }

    if (!isConverting && !isPreparingConversion && trackedDownloadState === 'importing') {
      title = translate('QueueStatusDownloadedImporting');
      iconKind = kinds.PRIMARY;
    }

    if (trackedDownloadState === 'importBlocked' || trackedDownloadState === 'importFailed') {
      title = `${translate('Downloaded')} - ${translate('ManualImport')} ${translate('Required')}`;
      iconKind = kinds.WARNING;
    }

    if (trackedDownloadState === 'failedPending') {
      title = translate('QueueStatusDownloadedWaitingToProcess');
      iconKind = kinds.DANGER;
    }
  }

  if (hasWarning && !isConverting && !isConversionFailed && !isConversionCancelled) {
    iconKind = kinds.WARNING;
  }

  if (isConversionFailed) {
    iconName = icons.DANGER;
    iconKind = kinds.DANGER;
    title = translate('ConversionFailed');
  }

  if (isConversionCancelled) {
    iconName = icons.STOP;
    iconKind = kinds.WARNING;
    title = translate('ConversionCancelled');
  }

  if (status === 'delay') {
    iconName = icons.PENDING;
    title = translate('QueueStatusPending');
  }

  if (status === 'downloadClientUnavailable') {
    iconName = icons.PENDING;
    iconKind = kinds.WARNING;
    title = translate('QueueStatusPendingDownloadClientUnavailable');
  }

  if (status === 'failed') {
    iconName = icons.DOWNLOADING;
    iconKind = kinds.DANGER;
    title = translate('DownloadFailed');
  }

  if (status === 'warning') {
    iconName = icons.DOWNLOADING;
    iconKind = kinds.WARNING;
    title = translate('QueueStatusDownloadWarningWithDetails', {
      details: errorMessage || translate('QueueStatusDownloadClientMoreDetails')
    });
  }

  if (hasError && !isConverting && !isConversionFailed && !isConversionCancelled) {
    if (status === 'completed') {
      iconName = icons.DOWNLOAD;
      iconKind = kinds.DANGER;
      title = `${translate('ImportFailed')}: ${sourceTitle}`;
    } else {
      iconName = icons.DOWNLOADING;
      iconKind = kinds.DANGER;
      title = translate('DownloadFailed');
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
        title={translateServerMessage(title)}
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
