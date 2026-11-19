import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ProtocolLabel from 'Activity/Queue/ProtocolLabel';
import BookFormats from 'Book/BookFormats';
import BookQuality from 'Book/BookQuality';
import IndexerFlags from 'Book/IndexerFlags';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import labelStyles from 'Components/Label.css';
import Link from 'Components/Link/Link';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import tableRowStyles from 'Components/Table/TableRow.css';
import Popover from 'Components/Tooltip/Popover';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import formatDateTime from 'Utilities/Date/formatDateTime';
import formatAge from 'Utilities/Number/formatAge';
import formatBytes from 'Utilities/Number/formatBytes';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';
import Peers from './Peers';
import styles from './InteractiveSearchRow.css';

function getDownloadIcon(isGrabbing, isGrabbed, grabError) {
  if (isGrabbing) {
    return icons.SPINNER;
  } else if (isGrabbed) {
    return icons.DOWNLOADING;
  } else if (grabError) {
    return icons.DOWNLOADING;
  }

  return icons.DOWNLOAD;
}

function getDownloadKind(isGrabbed, grabError, downloadAllowed) {
  if (isGrabbed) {
    return kinds.SUCCESS;
  }

  if (grabError || !downloadAllowed) {
    return kinds.DANGER;
  }

  return kinds.DEFAULT;
}

function getDownloadTooltip(isGrabbing, isGrabbed, grabError) {
  if (isGrabbing) {
    return '';
  } else if (isGrabbed) {
    return translate('AddedToDownloadedQueue');
  } else if (grabError) {
    return grabError;
  }

  return translate('AddToDownloadedQueue');
}

function hasResolvedGrabTarget(authorId, bookId) {
  return authorId != null && bookId != null;
}

function getSearchScope({ searchId, searchedBookId, searchPayload }) {
  return {
    searchId: searchId ?? searchPayload.searchId,
    searchedBookId: searchedBookId ?? searchPayload.searchedBookId
  };
}

function isScopedGrabTargetValid(bookId, searchScope) {
  const isScopedSearch = searchScope.searchId != null || searchScope.searchedBookId != null;

  return !isScopedSearch || (
    searchScope.searchId != null &&
    searchScope.searchedBookId != null &&
    bookId === searchScope.searchedBookId
  );
}

function renderTitleCharSpan(title, matchedTitleCharStart, matchedTitleCharEnd) {
  if (!Number.isInteger(matchedTitleCharStart) || !Number.isInteger(matchedTitleCharEnd)) {
    return null;
  }

  if (matchedTitleCharStart < 0 || matchedTitleCharEnd <= matchedTitleCharStart || matchedTitleCharEnd > title.length) {
    return null;
  }

  return (
    <>
      {title.slice(0, matchedTitleCharStart)}
      <span className={styles.titleMatch}>{title.slice(matchedTitleCharStart, matchedTitleCharEnd)}</span>
      {title.slice(matchedTitleCharEnd)}
    </>
  );
}

function renderTitleWithMatch(title, matchedTitleCharStart, matchedTitleCharEnd) {
  if (!title) {
    return title;
  }

  return renderTitleCharSpan(title, matchedTitleCharStart, matchedTitleCharEnd) || title;
}

function getNarratorDisplay(narrator) {
  const rawNarrator = typeof narrator === 'string' ? narrator.trim() : '';

  if (!rawNarrator) {
    return {
      displayName: '',
      hasMore: false,
      tooltip: null
    };
  }

  const hasPlusMore = (/\+\d+\s*$/).test(rawNarrator);
  const narratorListText = rawNarrator.replace(/\s*\+\d+\s*$/, '').trim();
  const narratorNames = narratorListText
    .split(/\s*,\s*/)
    .map((name) => name.trim())
    .filter((name) => name);

  const displayName = narratorNames[0] || rawNarrator;
  const hasMore = hasPlusMore || narratorNames.length > 1;
  const tooltipNames = !hasPlusMore && narratorNames.length > 1 ? narratorNames : [rawNarrator];

  return {
    displayName,
    hasMore,
    tooltip: tooltipNames
  };
}

function renderNarratorDisplay(narrator) {
  const narratorDisplay = getNarratorDisplay(narrator);

  if (!narratorDisplay.displayName) {
    return null;
  }

  if (!narratorDisplay.hasMore) {
    return (
      <span
        className={styles.narratorName}
        title={narrator || ''}
      >
        {narratorDisplay.displayName}
      </span>
    );
  }

  return (
    <Tooltip
      anchor={
        <span
          className={styles.narratorName}
          title={narrator || ''}
        >
          {narratorDisplay.displayName}
          <span className={styles.moreIndicator}>{', …'}</span>
        </span>
      }
      tooltip={
        <div>
          {narratorDisplay.tooltip.map((name) => (
            <div key={name}>{name}</div>
          ))}
        </div>
      }
      position={tooltipPositions.TOP}
    />
  );
}

class InteractiveSearchRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isConfirmGrabModalOpen: false
    };
  }

  componentDidMount() {
    this.scheduleExpiry();
  }

  componentDidUpdate(previous) {
    if (previous.expiresAt !== this.props.expiresAt) {
      this.scheduleExpiry();
    }
  }

  componentWillUnmount() {
    clearTimeout(this._expiryTimer);
  }

  isExpired = () => this.props.expiresAt && Date.parse(this.props.expiresAt) <= Date.now();

  scheduleExpiry = () => {
    clearTimeout(this._expiryTimer);
    const remaining = Date.parse(this.props.expiresAt) - Date.now();
    if (remaining > 0) {
      this._expiryTimer = setTimeout(() => this.forceUpdate(), remaining + 1);
    }
  };

  //
  // Listeners

  onGrabPress = () => {
    if (this.isExpired()) {
      return;
    }
    const {
      guid,
      indexerId,
      authorId,
      bookId,
      onGrabPress
    } = this.props;
    const payload = {
      guid,
      indexerId,
      authorId,
      bookId
    };
    const searchScope = getSearchScope(this.props);

    if (searchScope.searchId != null && searchScope.searchedBookId != null) {
      payload.searchId = searchScope.searchId;
      payload.searchedBookId = searchScope.searchedBookId;
    }

    onGrabPress(payload);
  };

  onConfirmGrabPress = () => {
    this.setState({ isConfirmGrabModalOpen: true });
  };

  onGrabConfirm = () => {
    if (this.isExpired()) {
      return;
    }
    this.setState({ isConfirmGrabModalOpen: false });

    const {
      guid,
      indexerId,
      authorId,
      bookId,
      searchPayload,
      onGrabPress
    } = this.props;
    const payload = {
      guid,
      indexerId,
      ...searchPayload
    };
    const searchScope = getSearchScope(this.props);

    if (searchScope.searchId != null && searchScope.searchedBookId != null) {
      payload.authorId = authorId;
      payload.bookId = bookId;
      payload.searchId = searchScope.searchId;
      payload.searchedBookId = searchScope.searchedBookId;
    }

    onGrabPress(payload);
  };

  onGrabCancel = () => {
    this.setState({ isConfirmGrabModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      rank,
      protocol,
      age,
      ageHours,
      ageMinutes,
      publishDate,
      title,
      displayTitle,
      matchedTitleCharStart,
      matchedTitleCharEnd,
      infoUrl,
      indexer,
      size,
      seeders,
      leechers,
      quality,
      customFormatScore,
      customFormats,
      indexerFlags = 0,
      isGraphicAudio,
      authorId,
      bookId,
      rejections,
      downloadAllowed,
      isGrabbing,
      isGrabbed,
      isPreferredChoice,
      showPreferredBadge,
      showNarrator,
      summaryMode,
      summaryBookLabel,
      summaryStatusText,
      longDateFormat,
      timeFormat,
      grabError
    } = this.props;

    const titleText = renderTitleWithMatch(displayTitle || title, matchedTitleCharStart, matchedTitleCharEnd);
    const narratorDisplay = renderNarratorDisplay(this.props.narrator);
    const resolvedGrabTarget = hasResolvedGrabTarget(authorId, bookId);
    const isValidScopedGrabTarget = isScopedGrabTargetValid(bookId, getSearchScope(this.props));
    const expired = this.isExpired();
    let downloadButtonTitle = isValidScopedGrabTarget ?
      getDownloadTooltip(isGrabbing, isGrabbed, grabError) :
      translate('InteractiveSearchGrabBookMismatch');
    if (expired) {
      downloadButtonTitle = translate('SearchResultsExpired');
    }
    const rowClassName = classNames(
      tableRowStyles.row,
      isPreferredChoice && styles.preferredRow
    );

    if (summaryMode) {
      return (
        <TableRow className={rowClassName}>
          <TableRowCell className={styles.summaryBook}>
            {summaryBookLabel}
            {summaryStatusText ? <div className={styles.summaryBookStatus}>{summaryStatusText}</div> : null}
          </TableRowCell>
          <TableRowCell className={styles.title}>
            <div className={classNames(styles.titleContent, !showPreferredBadge && styles.titleContentWithoutBadge)}>
              <div className={styles.titleTextBlock}>
                <Link
                  to={infoUrl}
                  title={title}
                  className={styles.titleLink}
                >
                  {titleText}
                </Link>
                {grabError ? <div className={styles.grabError}>{grabError}</div> : null}
              </div>
              {isPreferredChoice && showPreferredBadge ? <span className={styles.preferredBadge}>{translate('Preferred')}</span> : null}
            </div>
          </TableRowCell>
          <TableRowCell className={styles.indexer}>{indexer}</TableRowCell>
          <TableRowCell className={styles.quality}>
            <BookQuality
              className={labelStyles.wrapping}
              quality={quality}
              showRevision={true}
            />
          </TableRowCell>
          <TableRowCell className={styles.size}>{formatBytes(size)}</TableRowCell>
          {showNarrator ?
            <TableRowCell className={styles.narrator}>
              <div className={styles.narratorContent}>{narratorDisplay}</div>
            </TableRowCell> : null}
          <TableRowCell className={styles.download}>
            <SpinnerIconButton
              name={getDownloadIcon(isGrabbing, isGrabbed, grabError)}
              kind={getDownloadKind(isGrabbed, grabError, downloadAllowed)}
              title={downloadButtonTitle}
              isDisabled={expired || !isValidScopedGrabTarget}
              isSpinning={isGrabbing}
              onPress={downloadAllowed || resolvedGrabTarget ? this.onGrabPress : this.onConfirmGrabPress}
            />
          </TableRowCell>
          <ConfirmModal
            isOpen={this.state.isConfirmGrabModalOpen}
            kind={kinds.WARNING}
            title={translate('GrabRelease')}
            message={translate('GrabReleaseMessageText', [title])}
            confirmLabel={translate('Grab')}
            onConfirm={this.onGrabConfirm}
            onCancel={this.onGrabCancel}
          />
        </TableRow>
      );
    }

    return (
      <TableRow className={rowClassName}>
        <TableRowCell className={styles.rank}>
          {rank}
        </TableRowCell>

        <TableRowCell className={styles.protocol}>
          <ProtocolLabel
            protocol={protocol}
          />
        </TableRowCell>

        <TableRowCell
          className={styles.age}
          title={formatDateTime(publishDate, longDateFormat, timeFormat, { includeSeconds: true })}
        >
          {formatAge(age, ageHours, ageMinutes)}
        </TableRowCell>

        <TableRowCell className={styles.title}>
          <div className={styles.titleContent}>
            <div className={styles.titleTextBlock}>
              <Link
                to={infoUrl}
                title={title}
                className={styles.titleLink}
              >
                {titleText}
              </Link>
              {grabError ? (
                <div className={styles.grabError}>
                  {grabError}
                </div>
              ) : null}
            </div>
            {isPreferredChoice && showPreferredBadge ? (
              <span className={styles.preferredBadge}>{translate('Preferred')}</span>
            ) : null}
          </div>
        </TableRowCell>

        <TableRowCell className={styles.indexer}>
          {indexer}
        </TableRowCell>

        <TableRowCell className={styles.size}>
          {formatBytes(size)}
        </TableRowCell>

        <TableRowCell className={styles.peers}>
          {
            protocol === 'torrent' &&
              <Peers
                seeders={seeders}
                leechers={leechers}
              />
          }
        </TableRowCell>

        <TableRowCell className={styles.duration}>
          {this.props.duration || ''}
        </TableRowCell>

        {showNarrator ?
          <TableRowCell className={styles.narrator}>
            <div className={styles.narratorContent}>
              {narratorDisplay}
              {isGraphicAudio ? (
                <Label
                  kind={kinds.INFO}
                  title={translate('InteractiveSearchDramatizedTitle')}
                >
                  {translate('InteractiveSearchDramatized')}
                </Label>
              ) : null}
            </div>
          </TableRowCell> : null}

        <TableRowCell className={styles.quality}>
          <BookQuality
            className={labelStyles.wrapping}
            quality={quality}
            showRevision={true}
          />
        </TableRowCell>

        <TableRowCell className={styles.customFormatScore}>
          <Tooltip
            anchor={
              formatCustomFormatScore(customFormatScore, customFormats.length)
            }
            tooltip={<BookFormats formats={customFormats} />}
            position={tooltipPositions.LEFT}
          />
        </TableRowCell>

        <TableRowCell className={styles.indexerFlags}>
          {indexerFlags ? (
            <Popover
              anchor={<Icon name={icons.FLAG} kind={kinds.PRIMARY} />}
              title={translate('IndexerFlags')}
              body={<IndexerFlags indexerFlags={indexerFlags} />}
              position={tooltipPositions.LEFT}
            />
          ) : null}
        </TableRowCell>

        <TableRowCell className={styles.rejected}>
          {
            !!rejections.length &&
              <Popover
                anchor={
                  <Icon
                    name={icons.DANGER}
                    kind={kinds.DANGER}
                  />
                }
                title={translate('ReleaseRejected')}
                body={
                  <ul>
                    {
                      rejections.map((rejection, index) => {
                        return (
                          <li key={index}>
                            {rejection}
                          </li>
                        );
                      })
                    }
                  </ul>
                }
                position={tooltipPositions.LEFT}
              />
          }
        </TableRowCell>

        <TableRowCell className={styles.download}>
          {
            <SpinnerIconButton
              name={getDownloadIcon(isGrabbing, isGrabbed, grabError)}
              kind={getDownloadKind(isGrabbed, grabError, downloadAllowed)}
              title={downloadButtonTitle}
              isDisabled={expired || !isValidScopedGrabTarget}
              isSpinning={isGrabbing}
              onPress={downloadAllowed || resolvedGrabTarget ? this.onGrabPress : this.onConfirmGrabPress}
            />
          }
        </TableRowCell>

        <ConfirmModal
          isOpen={this.state.isConfirmGrabModalOpen}
          kind={kinds.WARNING}
          title={translate('GrabRelease')}
          message={translate('GrabReleaseMessageText', [title])}
          confirmLabel={translate('Grab')}
          onConfirm={this.onGrabConfirm}
          onCancel={this.onGrabCancel}
        />
      </TableRow>
    );
  }
}

InteractiveSearchRow.propTypes = {
  expiresAt: PropTypes.string,
  rank: PropTypes.number.isRequired,
  guid: PropTypes.string.isRequired,
  protocol: PropTypes.string.isRequired,
  age: PropTypes.number.isRequired,
  ageHours: PropTypes.number.isRequired,
  ageMinutes: PropTypes.number.isRequired,
  publishDate: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  displayTitle: PropTypes.string,
  bookTitle: PropTypes.string,
  matchedTitleCharStart: PropTypes.number,
  matchedTitleCharEnd: PropTypes.number,
  infoUrl: PropTypes.string.isRequired,
  indexer: PropTypes.string.isRequired,
  indexerId: PropTypes.number.isRequired,
  size: PropTypes.number.isRequired,
  duration: PropTypes.string,
  narrator: PropTypes.string,
  seeders: PropTypes.number,
  leechers: PropTypes.number,
  quality: PropTypes.object.isRequired,
  customFormats: PropTypes.arrayOf(PropTypes.object).isRequired,
  customFormatScore: PropTypes.number.isRequired,
  indexerFlags: PropTypes.number.isRequired,
  isGraphicAudio: PropTypes.bool,
  authorId: PropTypes.number,
  bookId: PropTypes.number,
  rejections: PropTypes.arrayOf(PropTypes.string).isRequired,
  downloadAllowed: PropTypes.bool.isRequired,
  isGrabbing: PropTypes.bool.isRequired,
  isGrabbed: PropTypes.bool.isRequired,
  isPreferredChoice: PropTypes.bool,
  showPreferredBadge: PropTypes.bool,
  showNarrator: PropTypes.bool,
  summaryMode: PropTypes.bool,
  summaryBookLabel: PropTypes.node,
  summaryStatusText: PropTypes.string,
  searchId: PropTypes.string,
  searchedBookId: PropTypes.number,
  grabError: PropTypes.string,
  longDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  searchPayload: PropTypes.object.isRequired,
  onGrabPress: PropTypes.func.isRequired,
  formatType: PropTypes.string
};

InteractiveSearchRow.defaultProps = {
  indexerFlags: 0,
  rejections: [],
  isGrabbing: false,
  isGrabbed: false,
  isPreferredChoice: false,
  showPreferredBadge: true,
  showNarrator: true,
  summaryMode: false,
  duration: null,
  narrator: null,
  isGraphicAudio: false
};

export default InteractiveSearchRow;
