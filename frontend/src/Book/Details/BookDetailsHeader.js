import moment from 'moment';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TextTruncate from 'react-text-truncate';
import AuthorNameLink from 'Author/AuthorNameLink';
import BookCover from 'Book/BookCover';
import HeartRating from 'Components/HeartRating';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import Marquee from 'Components/Marquee';
import Measure from 'Components/Measure';
import MonitorToggleButton from 'Components/MonitorToggleButton';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, sizes, tooltipPositions } from 'Helpers/Props';
import fonts from 'Styles/Variables/fonts';
import formatDurationMinutes from 'Utilities/Date/formatDurationMinutes';
import formatBytes from 'Utilities/Number/formatBytes';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import BookDetailsLinks from './BookDetailsLinks';
import styles from './BookDetailsHeader.css';

const defaultFontSize = parseInt(fonts.defaultFontSize);
const lineHeight = parseFloat(fonts.lineHeight);

function getFanartUrl(images) {
  return images.find((x) => x.coverType === 'fanart')?.url;
}

function getDownloadStatusLabel(queueItem, grabbed) {
  if (!queueItem && !grabbed) {
    return null;
  }

  const conversionStatus = queueItem?.conversionStatus;
  const convertToQuality = queueItem?.convertToQuality;
  const trackedDownloadState = queueItem?.trackedDownloadState;
  const trackedDownloadStatus = queueItem?.trackedDownloadStatus;

  if (conversionStatus === 'queued') {
    return {
      kind: kinds.PURPLE,
      icon: icons.QUEUED,
      title: convertToQuality ?
        translate('ConversionQueuedToQuality', { quality: convertToQuality }) :
        translate('ConversionQueued')
    };
  }

  if (conversionStatus === 'ready_to_import') {
    return {
      kind: kinds.PURPLE,
      icon: icons.DOWNLOADED,
      title: translate('ConvertedWaitingToImport')
    };
  }

  if (conversionStatus === 'failed') {
    return { kind: kinds.DANGER, icon: icons.REFRESH, title: 'Conversion failed' };
  }

  if (conversionStatus === 'cancelled') {
    return { kind: kinds.WARNING, icon: icons.STOP, title: 'Conversion cancelled' };
  }

  if (conversionStatus === 'converting' || conversionStatus === 'cancelling') {
    return {
      kind: kinds.PRIMARY,
      icon: icons.REFRESH,
      title: conversionStatus === 'cancelling' ?
        'Cancelling conversion' :
        `Converting${convertToQuality ? ` to ${convertToQuality}` : ''}`
    };
  }

  if (conversionStatus === 'preparing') {
    return {
      kind: kinds.PRIMARY,
      icon: icons.REFRESH,
      title: `Preparing conversion${convertToQuality ? ` to ${convertToQuality}` : ''}`
    };
  }

  if (conversionStatus === 'waiting') {
    return {
      kind: kinds.PURPLE,
      icon: icons.REFRESH,
      title: `Waiting to convert${convertToQuality ? ` to ${convertToQuality}` : ''}`
    };
  }

  if (trackedDownloadState === 'failedPending') {
    return { kind: kinds.DANGER, icon: icons.DOWNLOADED, title: 'Import failed - waiting to process' };
  }

  if (trackedDownloadState === 'importing') {
    return { kind: kinds.PRIMARY, icon: icons.DOWNLOADED, title: 'Importing' };
  }

  if (trackedDownloadState === 'importPending') {
    return { kind: kinds.PURPLE, icon: icons.DOWNLOADED, title: 'Waiting to import' };
  }

  if (trackedDownloadState === 'importBlocked') {
    return { kind: kinds.WARNING, icon: icons.DOWNLOADED, title: `${translate('ManualImport')} ${translate('Required')}` };
  }

  if (trackedDownloadStatus === 'error') {
    return { kind: kinds.DANGER, icon: icons.DOWNLOADING, title: translate('BookIsDownloading') };
  }

  if (trackedDownloadStatus === 'warning') {
    return { kind: kinds.WARNING, icon: icons.DOWNLOADING, title: translate('BookIsDownloading') };
  }

  return { kind: kinds.PRIMARY, icon: icons.DOWNLOADING, title: translate('BookIsDownloading') };
}

class BookDetailsHeader extends Component {

  //
  // Lifecycle

  constructor(props) {
    super(props);

    this.state = {
      overviewHeight: 0,
      titleWidth: 0
    };
  }

  //
  // Listeners

  onOverviewMeasure = ({ height }) => {
    this.setState({ overviewHeight: height });
  };

  onTitleMeasure = ({ width }) => {
    this.setState({ titleWidth: width });
  };

  //
  // Render

  render() {
    const {
      width,
      titleSlug,
      title,
      seriesTitle,
      pageCount,
      overview,
      statistics = {},
      grabbed,
      audiobookMonitored,
      ebookMonitored,
      mediaType,
      narrator,
      durationMinutes,
      releaseDate,
      ratings,
      images,
      links,
      isSaving,
      shortDateFormat,
      author,
      queueItem,
      isSmallScreen,
      onMonitorTogglePress
    } = this.props;

    const {
      overviewHeight,
      titleWidth
    } = this.state;

    const fanartUrl = getFanartUrl(author.images);
    const marqueeWidth = titleWidth - (isSmallScreen ? 85 : 160);
    const isDownloading = !!queueItem || grabbed;
    const downloadStatus = getDownloadStatusLabel(queueItem, grabbed);
    const downloadStatusLabel = downloadStatus && (
      <Label
        className={styles.detailsLabel}
        kind={downloadStatus.kind}
        size={sizes.LARGE}
      >
        <Icon
          name={downloadStatus.icon}
          size={17}
        />

        <span className={styles.qualityProfileName}>
          {downloadStatus.title}
        </span>
      </Label>
    );

    return (
      <div className={styles.header} style={{ width }}>
        <div
          className={styles.backdrop}
          style={
            fanartUrl ?
              { backgroundImage: `url(${fanartUrl})` } :
              null
          }
        >
          <div className={styles.backdropOverlay} />
        </div>

        <div className={styles.headerContent}>
          <BookCover
            className={styles.cover}
            images={images}
            size={250}
            lazy={false}
          />

          <div className={styles.info}>
            <Measure
              className={styles.titleRow}
              onMeasure={this.onTitleMeasure}
            >
              <div className={styles.titleContainer}>
                <div className={styles.toggleMonitoredContainer}>
                  <MonitorToggleButton
                    className={styles.monitorToggleButton}
                    monitored={mediaType === 'audiobook' ? audiobookMonitored : ebookMonitored}
                    isSaving={isSaving}
                    size={isSmallScreen ? 30 : 40}
                    isBinary={true}
                    onPress={onMonitorTogglePress}
                  />
                </div>

                <div className={styles.title} style={{ width: marqueeWidth }}>
                  <Marquee text={title} />
                </div>

              </div>
            </Measure>

            <div className={styles.details}>
              <div>
                {seriesTitle}
              </div>

              <div>
                <AuthorNameLink
                  className={styles.authorLink}
                  titleSlug={author.titleSlug}
                  authorId={author.id}
                  authorName={author.authorName}
                />

                {
                  mediaType === 'audiobook' && !!(narrator && narrator.trim?.()) &&
                    <span className={styles.duration}>
                      {translate('ReadByNarrator', { narrator: narrator.trim() })}
                    </span>
                }

                {
                  mediaType === 'audiobook' && !!durationMinutes &&
                    <span className={styles.duration}>
                      {formatDurationMinutes(durationMinutes)}
                    </span>
                }

                {
                  mediaType !== 'audiobook' && !!pageCount &&
                    <span className={styles.duration}>
                      {`${pageCount} pages`}
                    </span>
                }

                <HeartRating
                  rating={ratings.value}
                  iconSize={20}
                />
              </div>
            </div>

            <div className={styles.detailsLabels}>
              <Label
                className={styles.detailsLabel}
                size={sizes.LARGE}
              >
                <Icon
                  name={mediaType === 'audiobook' ? icons.HEADPHONES : icons.BOOK}
                  size={17}
                />

                <span className={styles.qualityProfileName}>
                  {mediaType === 'audiobook' ?
                    translate('AudiobookLabel') :
                    translate('EbookLabel')}
                </span>
              </Label>

              {
                releaseDate &&
                  <Label
                    className={styles.detailsLabel}
                    size={sizes.LARGE}
                  >
                    <Icon
                      name={icons.CALENDAR}
                      size={17}
                    />

                    <span className={styles.sizeOnDisk}>
                      {
                        moment(releaseDate).format(shortDateFormat)
                      }
                    </span>
                  </Label>
              }

              <Label
                className={styles.detailsLabel}
                size={sizes.LARGE}
              >
                <Icon
                  name={icons.DRIVE}
                  size={17}
                />

                <span className={styles.sizeOnDisk}>
                  {
                    formatBytes(statistics.sizeOnDisk)
                  }
                </span>
              </Label>

              {
                isDownloading &&
                  <Link
                    className={styles.downloadStatusLink}
                    to="/activity/queue"
                    title={translate('Queue')}
                  >
                    {downloadStatusLabel}
                  </Link>
              }

              <Label
                className={styles.detailsLabel}
                size={sizes.LARGE}
              >
                <Icon
                  name={(mediaType === 'audiobook' ? audiobookMonitored : ebookMonitored) ? icons.MONITORED : icons.UNMONITORED}
                  size={17}
                />

                <span className={styles.qualityProfileName}>
                  {(mediaType === 'audiobook' ? audiobookMonitored : ebookMonitored) ? 'Monitored' : 'Not Monitored'}
                </span>
              </Label>

              <Tooltip
                anchor={
                  <Label
                    className={styles.detailsLabel}
                    size={sizes.LARGE}
                  >
                    <Icon
                      name={icons.EXTERNAL_LINK}
                      size={17}
                    />

                    <span className={styles.links}>
                      {translate('Links')}
                    </span>
                  </Label>
                }
                tooltip={
                  <BookDetailsLinks
                    titleSlug={titleSlug}
                    links={links}
                  />
                }
                kind={kinds.INVERSE}
                position={tooltipPositions.BOTTOM}
              />

            </div>
            <Measure
              onMeasure={this.onOverviewMeasure}
              className={styles.overview}
            >
              <TextTruncate
                line={Math.floor(overviewHeight / (defaultFontSize * lineHeight))}
                text={stripHtml(overview)}
              />
            </Measure>
          </div>
        </div>
      </div>
    );
  }
}

BookDetailsHeader.propTypes = {
  id: PropTypes.number.isRequired,
  width: PropTypes.number.isRequired,
  titleSlug: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  seriesTitle: PropTypes.string.isRequired,
  pageCount: PropTypes.number,
  overview: PropTypes.string,
  statistics: PropTypes.object.isRequired,
  releaseDate: PropTypes.string.isRequired,
  ratings: PropTypes.object.isRequired,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  links: PropTypes.arrayOf(PropTypes.object).isRequired,
  grabbed: PropTypes.bool,
  audiobookMonitored: PropTypes.bool,
  ebookMonitored: PropTypes.bool,
  mediaType: PropTypes.string,
  narrator: PropTypes.string,
  durationMinutes: PropTypes.number,
  shortDateFormat: PropTypes.string.isRequired,
  isSaving: PropTypes.bool.isRequired,
  author: PropTypes.object,
  queueItem: PropTypes.object,
  isSmallScreen: PropTypes.bool.isRequired,
  onMonitorTogglePress: PropTypes.func.isRequired
};

BookDetailsHeader.defaultProps = {
  isSaving: false
};

export default BookDetailsHeader;
