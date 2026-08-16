import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorPoster from 'Author/AuthorPoster';
import DeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import EditAuthorModalConnector from 'Author/Edit/EditAuthorModalConnector';
import AuthorIndexProgressBar from 'Author/Index/ProgressBar/AuthorIndexProgressBar';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import Link from 'Components/Link/Link';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import { icons } from 'Helpers/Props';
import getRelativeDate from 'Utilities/Date/getRelativeDate';
import translate from 'Utilities/String/translate';
import shouldIgnoreCardSelectionEvent from 'Utilities/Table/shouldIgnoreCardSelectionEvent';
import AuthorIndexPosterInfo from './AuthorIndexPosterInfo';
import styles from './AuthorIndexPoster.css';

function getMediaTypeLabel(mediaType) {
  return translate(mediaType === 'ebook' ? 'Ebooks' : 'Audiobooks');
}

function getMonitorExistingLabel(value) {
  if (value === 1) {
    return translate('AllBooks');
  }

  if (value === 2) {
    return translate('SelectBooks');
  }

  return translate('NoBooks');
}

function getMediaTypeIcon(mediaType) {
  return mediaType === 'ebook' ? icons.BOOK : icons.HEADPHONES;
}

class AuthorIndexPoster extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      hasPosterError: false,
      isEditAuthorModalOpen: false,
      isDeleteAuthorModalOpen: false
    };
  }

  componentDidUpdate(prevProps) {
    const { images } = this.props;
    const prevImages = prevProps.images;

    // Reset poster error if images have changed
    if (images && prevImages && images[0]?.url !== prevImages[0]?.url) {
      if (this.state.hasPosterError) {
        this.setState({ hasPosterError: false });
      }
    }
  }

  //
  // Listeners

  onEditAuthorPress = () => {
    this.setState({ isEditAuthorModalOpen: true });
  };

  onEditAuthorModalClose = () => {
    this.setState({ isEditAuthorModalOpen: false });
  };

  onDeleteAuthorPress = () => {
    this.setState({
      isEditAuthorModalOpen: false,
      isDeleteAuthorModalOpen: true
    });
  };

  onDeleteAuthorModalClose = () => {
    this.setState({ isDeleteAuthorModalOpen: false });
  };

  onPosterLoad = () => {
    if (this.state.hasPosterError) {
      this.setState({ hasPosterError: false });
    }
  };

  onPosterLoadError = () => {
    if (!this.state.hasPosterError) {
      this.setState({ hasPosterError: true });
    }
  };

  onChange = ({ value, shiftKey }) => {
    const {
      id,
      onSelectedChange
    } = this.props;

    onSelectedChange({ id, value, shiftKey });
  };

  onCardPress = (event) => {
    const { id, isEditorActive, isSelected } = this.props;

    if (!isEditorActive || shouldIgnoreCardSelectionEvent(event)) {
      return;
    }

    if (event.ctrlKey || event.metaKey) {
      window.open(`${window.Chaptarr.urlBase}/author/${id}`, '_blank');
      return;
    }

    event.preventDefault();
    this.onChange({ value: !isSelected, shiftKey: event.shiftKey });
  };

  onCardKeyDown = (event) => {
    const { isEditorActive, isSelected } = this.props;

    if (!isEditorActive || event.target !== event.currentTarget) {
      return;
    }

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.onChange({ value: !isSelected, shiftKey: event.shiftKey });
    }
  };

  //
  // Render

  render() {
    const {
      id,
      authorName,
      authorNameLastFirst,
      monitored,
      titleSlug,
      status,
      nextAiring,
      statistics = {},
      images,
      selectedPosterHash,
      posterWidth,
      posterHeight,
      detailedProgressBar,
      cropPosters,
      showTitle,
      showMonitored,
      showQualityProfile,
      qualityProfile,
      mediaTypeDetails = [],
      selectedMediaType,
      metadataProfile,
      showSearchAction,
      showRelativeDates,
      shortDateFormat,
      timeFormat,
      isRefreshingAuthor,
      isSearchingAuthor,
      onRefreshAuthorPress,
      onSearchPress,
      isEditorActive,
      isSelected,
      onSelectedChange,
      ...otherProps
    } = this.props;

    const {
      bookCount = 0,
      availableBookCount = 0,
      bookFileCount = 0,
      totalBookCount = 0,
      sizeOnDisk = 0
    } = statistics;

    const {
      hasPosterError,
      isEditAuthorModalOpen,
      isDeleteAuthorModalOpen
    } = this.state;

    const link = `/author/${id}`;
    const contentClassName = classNames(
      styles.content,
      isEditorActive && styles.selectable
    );

    const editorInteractionProps = isEditorActive ? {
      onClick: this.onCardPress,
      onKeyDown: this.onCardKeyDown,
      role: 'button',
      tabIndex: 0,
      'aria-pressed': !!isSelected
    } : {};

    const elementStyle = {
      width: `${posterWidth}px`,
      height: `${posterHeight}px`
    };

    const posterStyle = {
      ...elementStyle,
      objectFit: cropPosters ? 'cover' : 'contain',
      objectPosition: cropPosters ? 'top' : 'center'
    };
    const visibleMediaTypeDetails = mediaTypeDetails.filter((details) => {
      return details.isConfigured &&
        (selectedMediaType === 'all' || details.mediaType === selectedMediaType);
    });
    const monitoringTitle = visibleMediaTypeDetails.map((details) => {
      const monitoredLabel = translate(details.monitored ? 'Monitored' : 'Unmonitored');
      const futureLabel = translate(details.monitorFuture ? 'Yes' : 'No');

      return `${getMediaTypeLabel(details.mediaType)}: ${monitoredLabel}; ${translate('ExistingBooks')}: ${getMonitorExistingLabel(details.monitorExisting)}; ${translate('MonitorFutureReleases')}: ${futureLabel}`;
    }).join('\n');
    const profileTitle = visibleMediaTypeDetails.map((details) => {
      return `${getMediaTypeLabel(details.mediaType)}: ${details.qualityProfile?.name ?? ''}`;
    }).join('\n');
    const isAudiobookMonitored = visibleMediaTypeDetails.some((details) => {
      return details.mediaType === 'audiobook' && details.monitored;
    });
    const isEbookMonitored = visibleMediaTypeDetails.some((details) => {
      return details.mediaType === 'ebook' && details.monitored;
    });
    const monitoredLabel = translate(monitored ? 'Monitored' : 'Unmonitored');
    const visibleProfiles = visibleMediaTypeDetails.filter((details) => !!details.qualityProfile?.name);
    const showProfileMediaType = selectedMediaType === 'all';

    return (
      <div className={styles.container}>
        <div
          className={contentClassName}
          {...editorInteractionProps}
        >
          <div className={styles.posterContainer}>
            {
              isEditorActive &&
                <div className={styles.editorSelect}>
                  <Icon
                    className={isSelected ? styles.selected : styles.unselected}
                    name={isSelected ? icons.CHECK_CIRCLE : icons.CIRCLE_OUTLINE}
                    size={20}
                  />
                </div>
            }

            <Label
              className={styles.controls}
              data-select-exempt="true"
            >
              <SpinnerIconButton
                className={styles.action}
                name={icons.REFRESH}
                title={translate('RefreshAuthor')}
                isSpinning={isRefreshingAuthor}
                onPress={onRefreshAuthorPress}
              />

              {
                showSearchAction &&
                  <SpinnerIconButton
                    className={styles.action}
                    name={icons.SEARCH}
                    title={translate('SearchForMonitoredBooks')}
                    isSpinning={isSearchingAuthor}
                    onPress={onSearchPress}
                  />
              }

              <IconButton
                className={styles.action}
                name={icons.EDIT}
                title={translate('EditAuthor')}
                onPress={this.onEditAuthorPress}
              />
            </Label>

            {
              status === 'ended' &&
                <div
                  className={styles.ended}
                  title={translate('Ended')}
                />
            }

            <Link
              className={styles.link}
              component={isEditorActive ? 'div' : undefined}
              style={elementStyle}
              to={isEditorActive ? undefined : link}
            >
              <AuthorPoster
                className={styles.poster}
                style={posterStyle}
                images={images}
                authorId={id}
                selectedPosterHash={selectedPosterHash}
                placeholder="/Content/Images/author-placeholder-muted.png"
                size={150}
                lazy={false}
                overflow={true}
                blurBackground={false}
                isEditorActive={isEditorActive}
                onError={this.onPosterLoadError}
                onLoad={this.onPosterLoad}
              />

              {
                hasPosterError &&
                  <div className={styles.overlayTitle}>
                    {authorName}
                  </div>
              }

            </Link>
          </div>

          <AuthorIndexProgressBar
            monitored={monitored}
            status={status}
            bookCount={bookCount}
            availableBookCount={availableBookCount}
            bookFileCount={bookFileCount}
            totalBookCount={totalBookCount}
            posterWidth={posterWidth}
            detailedProgressBar={detailedProgressBar}
          />

          {
            showTitle !== 'no' &&
              <div className={styles.title}>
                {showTitle === 'firstLast' ? authorName : authorNameLastFirst}
              </div>
          }

          {
            showMonitored &&
              <div
                className={classNames(styles.mediaSummary, styles.monitoringSummary)}
                title={monitoringTitle}
                aria-label={monitoringTitle}
              >
                {
                  isAudiobookMonitored &&
                    <Icon
                      className={styles.monitoringIconStart}
                      name={getMediaTypeIcon('audiobook')}
                    />
                }

                <span className={styles.monitoringLabel}>
                  {monitoredLabel}
                </span>

                {
                  isEbookMonitored &&
                    <Icon
                      className={styles.monitoringIconEnd}
                      name={getMediaTypeIcon('ebook')}
                    />
                }
              </div>
          }

          {
            showQualityProfile && visibleProfiles.length > 0 &&
              <div
                className={styles.mediaSummary}
                title={profileTitle}
                aria-label={profileTitle}
              >
                {
                  visibleProfiles.map((details) => {
                    return (
                      <span
                        key={details.mediaType}
                        className={styles.mediaProfile}
                      >
                        {
                          showProfileMediaType &&
                            <Icon
                              className={styles.mediaProfileIcon}
                              name={getMediaTypeIcon(details.mediaType)}
                            />
                        }
                        <span className={styles.profileName}>
                          {details.qualityProfile.name}
                        </span>
                      </span>
                    );
                  })
                }
              </div>
          }

          {
            nextAiring &&
              <div className={styles.nextAiring}>
                {
                  getRelativeDate(
                    nextAiring,
                    shortDateFormat,
                    showRelativeDates,
                    {
                      timeFormat,
                      timeForToday: true
                    }
                  )
                }
              </div>
          }
          <AuthorIndexPosterInfo
            bookCount={bookCount}
            sizeOnDisk={sizeOnDisk}
            qualityProfile={qualityProfile}
            showQualityProfile={showQualityProfile}
            metadataProfile={metadataProfile}
            showRelativeDates={showRelativeDates}
            shortDateFormat={shortDateFormat}
            timeFormat={timeFormat}
            {...otherProps}
          />

          <EditAuthorModalConnector
            isOpen={isEditAuthorModalOpen}
            authorId={id}
            onModalClose={this.onEditAuthorModalClose}
            onDeleteAuthorPress={this.onDeleteAuthorPress}
          />

          <DeleteAuthorModal
            isOpen={isDeleteAuthorModalOpen}
            authorId={id}
            onModalClose={this.onDeleteAuthorModalClose}
          />
        </div>
      </div>
    );
  }
}

AuthorIndexPoster.propTypes = {
  id: PropTypes.number.isRequired,
  authorName: PropTypes.string.isRequired,
  authorNameLastFirst: PropTypes.string.isRequired,
  monitored: PropTypes.bool.isRequired,
  status: PropTypes.string.isRequired,
  titleSlug: PropTypes.string.isRequired,
  nextAiring: PropTypes.string,
  statistics: PropTypes.object.isRequired,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectedPosterHash: PropTypes.string,
  posterWidth: PropTypes.number.isRequired,
  posterHeight: PropTypes.number.isRequired,
  detailedProgressBar: PropTypes.bool.isRequired,
  cropPosters: PropTypes.bool.isRequired,
  showTitle: PropTypes.string.isRequired,
  showMonitored: PropTypes.bool.isRequired,
  showQualityProfile: PropTypes.bool.isRequired,
  qualityProfile: PropTypes.object,
  mediaTypeDetails: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'all', 'ebook']).isRequired,
  metadataProfile: PropTypes.object,
  showSearchAction: PropTypes.bool.isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  isRefreshingAuthor: PropTypes.bool.isRequired,
  isSearchingAuthor: PropTypes.bool.isRequired,
  onRefreshAuthorPress: PropTypes.func.isRequired,
  onSearchPress: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

AuthorIndexPoster.defaultProps = {
  statistics: {
    bookCount: 0,
    bookFileCount: 0,
    totalBookCount: 0
  }
};

export default AuthorIndexPoster;
