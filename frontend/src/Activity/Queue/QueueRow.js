import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ProtocolLabel from 'Activity/Queue/ProtocolLabel';
import AuthorNameLink from 'Author/AuthorNameLink';
import BookFormats from 'Book/BookFormats';
import BookQuality from 'Book/BookQuality';
import BookTitleLink from 'Book/BookTitleLink';
import IconButton from 'Components/Link/IconButton';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import ProgressBar from 'Components/ProgressBar';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import InteractiveImportModal from 'InteractiveImport/InteractiveImportModal';
import formatBytes from 'Utilities/Number/formatBytes';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';
import QueueStatusCell from './QueueStatusCell';
import RemoveQueueItemModal from './RemoveQueueItemModal';
import TimeleftCell from './TimeleftCell';
import styles from './QueueRow.css';

function getConversionProgressLabel(message, progress) {
  const normalizedMessage = message?.toLowerCase() ?? '';

  if (normalizedMessage.startsWith('cancelling')) {
    return translate('Cancelling');
  }

  if (normalizedMessage.startsWith('cancelled')) {
    return 'Cancelled';
  }

  if (normalizedMessage.startsWith('verifying')) {
    return 'Verifying';
  }

  if (normalizedMessage.startsWith('finalizing')) {
    return 'Finalizing';
  }

  if (normalizedMessage.startsWith('preparing import')) {
    return 'Preparing import';
  }

  if (normalizedMessage.startsWith('importing')) {
    return 'Importing';
  }

  if (normalizedMessage.startsWith('converted m4b imported')) {
    return 'Imported';
  }

  const stepMatch = message?.match(/(\d+)\s+of\s+(\d+)/i);

  if (stepMatch) {
    return `${stepMatch[1]} of ${stepMatch[2]}`;
  }

  return `${progress.toFixed(1)}%`;
}

class QueueRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isRemoveQueueItemModalOpen: false,
      isInteractiveImportModalOpen: false
    };
  }

  //
  // Listeners

  onRemoveQueueItemPress = () => {
    this.setState({ isRemoveQueueItemModalOpen: true });
  };

  onRemoveQueueItemModalConfirmed = (blocklist, skipRedownload) => {
    const {
      onRemoveQueueItemPress,
      onQueueRowModalOpenOrClose
    } = this.props;

    onQueueRowModalOpenOrClose(false);
    onRemoveQueueItemPress(blocklist, skipRedownload);

    this.setState({ isRemoveQueueItemModalOpen: false });
  };

  onRemoveQueueItemModalClose = () => {
    this.props.onQueueRowModalOpenOrClose(false);

    this.setState({ isRemoveQueueItemModalOpen: false });
  };

  onInteractiveImportPress = () => {
    this.props.onQueueRowModalOpenOrClose(true);

    this.setState({ isInteractiveImportModalOpen: true });
  };

  onInteractiveImportModalClose = () => {
    this.props.onQueueRowModalOpenOrClose(false);

    this.setState({ isInteractiveImportModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      id,
      downloadId,
      title,
      status,
      trackedDownloadStatus,
      trackedDownloadState,
      statusMessages,
      errorMessage,
      conversionStatus,
      convertToQualityId,
      convertToQuality,
      conversionProgress,
      conversionMessage,
      canCancelConversion,
      canRetryImport,
      author,
      book,
      quality,
      customFormats,
      customFormatScore,
      protocol,
      indexer,
      outputPath,
      downloadClient,
      downloadClientHasPostImportCategory,
      estimatedCompletionTime,
      added,
      timeleft,
      size,
      sizeleft,
      showRelativeDates,
      shortDateFormat,
      timeFormat,
      isGrabbing,
      grabError,
      isRemoving,
      isCancellingConversion,
      isRetryingImport,
      isSelected,
      columns,
      onSelectedChange,
      onGrabPress,
      onCancelConversionPress,
      onRetryImportPress
    } = this.props;

    const {
      isRemoveQueueItemModalOpen,
      isInteractiveImportModalOpen
    } = this.state;

    const downloadProgress = size ? Math.max(0, Math.min(100, 100 - (sizeleft / size * 100))) : 0;
    const isConverting = conversionStatus === 'converting' || conversionStatus === 'cancelling';
    const conversionProgressValue = conversionProgress == null ? 0 : Math.max(0, Math.min(100, conversionProgress));
    const displayConversionProgress = isConverting ? Math.max(1, conversionProgressValue) : conversionProgressValue;
    const conversionProgressLabel = getConversionProgressLabel(conversionMessage, displayConversionProgress);
    const convertToQualityModel = convertToQuality ?
      {
        quality: {
          id: convertToQualityId,
          name: convertToQuality
        },
        revision: {
          version: 1,
          real: 0,
          isRepack: false
        }
      } :
      null;
    const showProgress = isConverting || !!downloadProgress;
    const showInteractiveImport = status === 'completed' && trackedDownloadStatus === 'warning';
    const showRetryImport = !!canRetryImport;
    const isPending = status === 'delay' || status === 'downloadClientUnavailable';

    return (
      <TableRow>
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
        />

        {
          columns.map((column) => {
            const {
              name,
              isVisible
            } = column;

            if (!isVisible) {
              return null;
            }

            if (name === 'status') {
              return (
                <QueueStatusCell
                  key={name}
                  sourceTitle={title}
                  status={status}
                  trackedDownloadStatus={trackedDownloadStatus}
                  trackedDownloadState={trackedDownloadState}
                  statusMessages={statusMessages}
                  errorMessage={errorMessage}
                  conversionStatus={conversionStatus}
                  conversionMessage={conversionMessage}
                  convertToQuality={convertToQuality}
                />
              );
            }

            if (name === 'authors.sortName') {
              return (
                <TableRowCell key={name}>
                  {
                    author ?
                      <AuthorNameLink
                        titleSlug={author.titleSlug}
                        authorId={author.id}
                        authorName={author.authorName}
                      /> :
                      title
                  }
                </TableRowCell>
              );
            }

            if (name === 'books.title') {
              return (
                <TableRowCell key={name}>
                  {
                    book ?
                      <BookTitleLink
                        titleSlug={book.titleSlug}
                        bookId={book.id}
                        title={book.title}
                        disambiguation={book.disambiguation}
                      /> :
                      '-'
                  }
                </TableRowCell>
              );
            }

            if (name === 'books.releaseDate') {
              if (book) {
                return (
                  <RelativeDateCellConnector
                    key={name}
                    date={book.releaseDate}
                  />
                );
              }

              return (
                <TableRowCell key={name}>
                  -
                </TableRowCell>
              );
            }

            if (name === 'quality') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.quality}
                >
                  <BookQuality
                    className={styles.qualityLabel}
                    quality={quality}
                  />
                </TableRowCell>
              );
            }

            if (name === 'convertToQuality') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.convertToQuality}
                >
                  {
                    convertToQualityModel ?
                      <BookQuality
                        className={styles.qualityLabel}
                        title={conversionMessage || ''}
                        quality={convertToQualityModel}
                      /> :
                      '-'
                  }
                </TableRowCell>
              );
            }

            if (name === 'customFormats') {
              return (
                <TableRowCell key={name}>
                  <BookFormats
                    formats={customFormats}
                  />
                </TableRowCell>
              );
            }

            if (name === 'customFormatScore') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.customFormatScore}
                >
                  <Tooltip
                    anchor={formatCustomFormatScore(
                      customFormatScore,
                      customFormats.length
                    )}
                    tooltip={<BookFormats formats={customFormats} />}
                    position={tooltipPositions.BOTTOM}
                  />
                </TableRowCell>
              );
            }

            if (name === 'protocol') {
              return (
                <TableRowCell key={name}>
                  <ProtocolLabel
                    protocol={protocol}
                  />
                </TableRowCell>
              );
            }

            if (name === 'indexer') {
              return (
                <TableRowCell key={name}>
                  {indexer}
                </TableRowCell>
              );
            }

            if (name === 'downloadClient') {
              return (
                <TableRowCell key={name}>
                  {downloadClient}
                </TableRowCell>
              );
            }

            if (name === 'title') {
              return (
                <TableRowCell key={name}>
                  {title}
                </TableRowCell>
              );
            }

            if (name === 'size') {
              return (
                <TableRowCell key={name}>{formatBytes(size)}</TableRowCell>
              );
            }

            if (name === 'outputPath') {
              return (
                <TableRowCell key={name}>
                  {outputPath}
                </TableRowCell>
              );
            }

            if (name === 'estimatedCompletionTime') {
              return (
                <TimeleftCell
                  key={name}
                  status={status}
                  estimatedCompletionTime={estimatedCompletionTime}
                  timeleft={timeleft}
                  size={size}
                  sizeleft={sizeleft}
                  showRelativeDates={showRelativeDates}
                  shortDateFormat={shortDateFormat}
                  timeFormat={timeFormat}
                />
              );
            }

            if (name === 'added') {
              return (
                <RelativeDateCellConnector
                  key={name}
                  date={added}
                />
              );
            }

            if (name === 'progress') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.progress}
                >
                  {
                    isConverting ?
                      <div
                        className={styles.conversionProgress}
                        title={conversionMessage || conversionProgressLabel}
                        role="progressbar"
                        aria-valuenow={Math.round(displayConversionProgress)}
                        aria-valuemin={0}
                        aria-valuemax={100}
                        aria-label={conversionMessage || conversionProgressLabel}
                      >
                        <div className={styles.conversionDownloadComplete} />
                        <div
                          className={styles.conversionProgressOverlay}
                          style={{ width: `${displayConversionProgress.toFixed(1)}%` }}
                        />
                        <div className={styles.conversionProgressLabel}>
                          {conversionProgressLabel}
                        </div>
                      </div> :
                      showProgress &&
                        <ProgressBar
                          progress={downloadProgress}
                          title={`${downloadProgress.toFixed(1)}%`}
                          kind={kinds.PRIMARY}
                        />
                  }
                </TableRowCell>
              );
            }

            if (name === 'actions') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.actions}
                >
                  {
                    showInteractiveImport &&
                      <IconButton
                        name={icons.INTERACTIVE}
                        onPress={this.onInteractiveImportPress}
                      />
                  }

                  {
                    (canCancelConversion || isCancellingConversion) &&
                      <SpinnerIconButton
                        title={translate('CancelConversion')}
                        name={icons.STOP}
                        kind={kinds.WARNING}
                        isSpinning={isCancellingConversion}
                        onPress={onCancelConversionPress}
                      />
                  }

                  {
                    showRetryImport &&
                      <SpinnerIconButton
                        title={translate('RetryImport')}
                        name={icons.RESTART}
                        isSpinning={isRetryingImport}
                        onPress={onRetryImportPress}
                      />
                  }

                  {
                    isPending &&
                      <SpinnerIconButton
                        name={icons.DOWNLOAD}
                        kind={grabError ? kinds.DANGER : kinds.DEFAULT}
                        isSpinning={isGrabbing}
                        onPress={onGrabPress}
                      />
                  }

                  <SpinnerIconButton
                    title={translate('RemoveFromQueue')}
                    name={icons.REMOVE}
                    isSpinning={isRemoving}
                    onPress={this.onRemoveQueueItemPress}
                  />
                </TableRowCell>
              );
            }

            return null;
          })
        }

        <InteractiveImportModal
          isOpen={isInteractiveImportModalOpen}
          downloadId={downloadId}
          title={title}
          onModalClose={this.onInteractiveImportModalClose}
          showReplaceExistingFiles={true}
          replaceExistingFiles={true}
        />

        <RemoveQueueItemModal
          isOpen={isRemoveQueueItemModalOpen}
          sourceTitle={title}
          canChangeCategory={!!downloadClientHasPostImportCategory}
          canIgnore={!!downloadId}
          isPending={isPending}
          onRemovePress={this.onRemoveQueueItemModalConfirmed}
          onModalClose={this.onRemoveQueueItemModalClose}
        />
      </TableRow>
    );
  }

}

QueueRow.propTypes = {
  id: PropTypes.number.isRequired,
  downloadId: PropTypes.string,
  title: PropTypes.string.isRequired,
  status: PropTypes.string.isRequired,
  trackedDownloadStatus: PropTypes.string,
  trackedDownloadState: PropTypes.string,
  statusMessages: PropTypes.arrayOf(PropTypes.object),
  errorMessage: PropTypes.string,
  conversionStatus: PropTypes.string,
  convertToQualityId: PropTypes.number,
  convertToQuality: PropTypes.string,
  conversionProgress: PropTypes.number,
  conversionMessage: PropTypes.string,
  canCancelConversion: PropTypes.bool.isRequired,
  canRetryImport: PropTypes.bool.isRequired,
  author: PropTypes.object,
  book: PropTypes.object,
  quality: PropTypes.object.isRequired,
  customFormats: PropTypes.arrayOf(PropTypes.object),
  customFormatScore: PropTypes.number.isRequired,
  protocol: PropTypes.string.isRequired,
  indexer: PropTypes.string,
  outputPath: PropTypes.string,
  downloadClient: PropTypes.string,
  downloadClientHasPostImportCategory: PropTypes.bool,
  estimatedCompletionTime: PropTypes.string,
  added: PropTypes.string,
  timeleft: PropTypes.string,
  size: PropTypes.number,
  sizeleft: PropTypes.number,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  isGrabbing: PropTypes.bool.isRequired,
  grabError: PropTypes.object,
  isRemoving: PropTypes.bool.isRequired,
  isCancellingConversion: PropTypes.bool.isRequired,
  isRetryingImport: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  onGrabPress: PropTypes.func.isRequired,
  onCancelConversionPress: PropTypes.func.isRequired,
  onRetryImportPress: PropTypes.func.isRequired,
  onRemoveQueueItemPress: PropTypes.func.isRequired,
  onQueueRowModalOpenOrClose: PropTypes.func.isRequired
};

QueueRow.defaultProps = {
  canRetryImport: false,
  canCancelConversion: false,
  customFormats: [],
  isGrabbing: false,
  isRemoving: false,
  isCancellingConversion: false,
  isRetryingImport: false
};

export default QueueRow;
