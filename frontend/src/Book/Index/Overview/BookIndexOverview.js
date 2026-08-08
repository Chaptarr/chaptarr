import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TextTruncate from 'react-text-truncate';
import DeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import EditAuthorModalConnector from 'Author/Edit/EditAuthorModalConnector';
import BookCover from 'Book/BookCover';
import BookIndexProgressBar from 'Book/Index/ProgressBar/BookIndexProgressBar';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import Link from 'Components/Link/Link';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import { icons } from 'Helpers/Props';
import dimensions from 'Styles/Variables/dimensions';
import fonts from 'Styles/Variables/fonts';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import shouldIgnoreCardSelectionEvent from 'Utilities/Table/shouldIgnoreCardSelectionEvent';
import BookIndexOverviewInfo from './BookIndexOverviewInfo';
import styles from './BookIndexOverview.css';

const columnPadding = parseInt(dimensions.authorIndexColumnPadding);
const columnPaddingSmallScreen = parseInt(dimensions.authorIndexColumnPaddingSmallScreen);
const defaultFontSize = parseInt(fonts.defaultFontSize);
const lineHeight = parseFloat(fonts.lineHeight);

// Hardcoded height beased on line-height of 32 + bottom margin of 10.
// Less side-effecty than using react-measure.
const titleRowHeight = 42;

function getContentHeight(rowHeight, isSmallScreen) {
  const padding = isSmallScreen ? columnPaddingSmallScreen : columnPadding;

  return rowHeight - padding;
}

class BookIndexOverview extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditAuthorModalOpen: false,
      isDeleteAuthorModalOpen: false,
      overview: ''
    };
  }

  componentDidMount() {
    const { id } = this.props;

    // Note that this component is lazy loaded by the virtualised view.
    // We want to avoid storing overviews for *all* books which is
    // why it's not put into the redux store
    const promise = createAjaxRequest({
      url: `/book/${id}/overview`
    }).request;

    promise.done((data) => {
      this.setState({ overview: data.overview });
    });
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
      window.open(`${window.Chaptarr.urlBase}/book/${id}`, '_blank');
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
      authorId,
      title,
      audiobookMonitored,
      ebookMonitored,
      titleSlug,
      nextAiring,
      statistics,
      images,
      posterWidth,
      posterHeight,
      qualityProfile,
      overviewOptions,
      showSearchAction,
      showRelativeDates,
      shortDateFormat,
      longDateFormat,
      timeFormat,
      rowHeight,
      isSmallScreen,
      isRefreshingBook,
      isSearchingBook,
      onRefreshBookPress,
      onSearchPress,
      isEditorActive,
      isSelected,
      ...otherProps
    } = this.props;

    const {
      bookCount,
      sizeOnDisk,
      bookFileCount,
      totalBookCount
    } = statistics;

    const {
      overview,
      isEditAuthorModalOpen,
      isDeleteAuthorModalOpen
    } = this.state;

    const link = `/book/${id}`;
    const containerClassName = classNames(
      styles.container,
      isEditorActive && styles.selectable
    );

    const elementStyle = {
      width: `${posterWidth}px`,
      height: `${posterHeight}px`,
      objectFit: 'contain'
    };

    const contentHeight = getContentHeight(rowHeight, isSmallScreen);
    const overviewHeight = contentHeight - titleRowHeight;

    return (
      <div
        className={containerClassName}
        onClick={this.onCardPress}
        onKeyDown={this.onCardKeyDown}
        role={isEditorActive ? 'button' : undefined}
        tabIndex={isEditorActive ? 0 : undefined}
        aria-pressed={isEditorActive ? !!isSelected : undefined}
      >
        <div className={styles.content}>
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

            <Link
              className={styles.link}
              component={isEditorActive ? 'div' : undefined}
              style={elementStyle}
              to={isEditorActive ? undefined : link}
            >
              <BookCover
                className={styles.poster}
                style={elementStyle}
                images={images}
                size={250}
                lazy={false}
                overflow={true}
                blurBackground={true}
              />
            </Link>

            <BookIndexProgressBar
              monitored={audiobookMonitored || ebookMonitored}
              bookCount={bookCount}
              bookFileCount={bookFileCount}
              totalBookCount={totalBookCount}
              posterWidth={posterWidth}
              detailedProgressBar={overviewOptions.detailedProgressBar}
            />
          </div>

          <div className={styles.info} style={{ maxHeight: contentHeight }}>
            <div className={styles.titleRow}>
              <Link
                className={styles.title}
                component={isEditorActive ? 'div' : undefined}
                to={isEditorActive ? undefined : link}
              >
                {title}
              </Link>

              <div
                className={styles.actions}
                data-select-exempt="true"
              >
                <SpinnerIconButton
                  name={icons.REFRESH}
                  title={translate('RefreshBook')}
                  isSpinning={isRefreshingBook}
                  onPress={onRefreshBookPress}
                />

                {
                  showSearchAction &&
                    <SpinnerIconButton
                      className={styles.action}
                      name={icons.SEARCH}
                      title={translate('SearchForMonitoredBooks')}
                      isSpinning={isSearchingBook}
                      onPress={onSearchPress}
                    />
                }

                <IconButton
                  name={icons.EDIT}
                  title={translate('EditAuthor')}
                  onPress={this.onEditAuthorPress}
                />
              </div>
            </div>

            <div className={styles.details}>

              <Link
                className={styles.overview}
                component={isEditorActive ? 'div' : undefined}
                to={isEditorActive ? undefined : link}
              >
                <TextTruncate
                  line={Math.floor(overviewHeight / (defaultFontSize * lineHeight))}
                  text={stripHtml(overview)}
                />
              </Link>

              <BookIndexOverviewInfo
                height={overviewHeight}
                audiobookMonitored={audiobookMonitored}
                ebookMonitored={ebookMonitored}
                sizeOnDisk={sizeOnDisk}
                qualityProfile={qualityProfile}
                showRelativeDates={showRelativeDates}
                shortDateFormat={shortDateFormat}
                longDateFormat={longDateFormat}
                timeFormat={timeFormat}
                {...overviewOptions}
                {...otherProps}
              />
            </div>
          </div>
        </div>

        <EditAuthorModalConnector
          isOpen={isEditAuthorModalOpen}
          authorId={authorId}
          onModalClose={this.onEditAuthorModalClose}
          onDeleteAuthorPress={this.onDeleteAuthorPress}
        />

        <DeleteAuthorModal
          isOpen={isDeleteAuthorModalOpen}
          authorId={authorId}
          onModalClose={this.onDeleteAuthorModalClose}
        />
      </div>
    );
  }
}

BookIndexOverview.propTypes = {
  id: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  audiobookMonitored: PropTypes.bool,
  ebookMonitored: PropTypes.bool,
  titleSlug: PropTypes.string.isRequired,
  nextAiring: PropTypes.string,
  statistics: PropTypes.object.isRequired,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  posterWidth: PropTypes.number.isRequired,
  posterHeight: PropTypes.number.isRequired,
  rowHeight: PropTypes.number.isRequired,
  qualityProfile: PropTypes.object.isRequired,
  overviewOptions: PropTypes.object.isRequired,
  showSearchAction: PropTypes.bool.isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  longDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  isRefreshingBook: PropTypes.bool.isRequired,
  isSearchingBook: PropTypes.bool.isRequired,
  onRefreshBookPress: PropTypes.func.isRequired,
  onSearchPress: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

BookIndexOverview.defaultProps = {
  statistics: {
    bookCount: 0,
    bookFileCount: 0,
    totalBookCount: 0
  }
};

export default BookIndexOverview;
