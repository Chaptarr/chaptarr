import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { Grid, WindowScroller } from 'react-virtualized';
import BookIndexItemConnector from 'Book/Index/BookIndexItemConnector';
import Measure from 'Components/Measure';
import dimensions from 'Styles/Variables/dimensions';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import BookIndexPoster from './BookIndexPoster';
import styles from './BookIndexPosters.css';

// Poster container dimensions
const columnPadding = parseInt(dimensions.authorIndexColumnPadding);
const columnPaddingSmallScreen = parseInt(dimensions.authorIndexColumnPaddingSmallScreen);
const progressBarHeight = parseInt(dimensions.progressBarSmallHeight);
const detailedProgressBarHeight = parseInt(dimensions.progressBarMediumHeight);

const additionalColumnCount = {
  small: 3,
  medium: 2,
  large: 1
};

function calculateColumnWidth(width, posterSize, isSmallScreen) {
  const maxiumColumnWidth = isSmallScreen ? 172 : 182;
  const columns = Math.floor(width / maxiumColumnWidth);
  const remainder = width % maxiumColumnWidth;

  if (remainder === 0 && posterSize === 'large') {
    return maxiumColumnWidth;
  }

  return Math.floor(width / (columns + additionalColumnCount[posterSize]));
}

function calculateRowHeight(posterHeight, sortKey, isSmallScreen, posterOptions) {
  const {
    detailedProgressBar,
    showTitle,
    showAuthor,
    showMonitored,
    showQualityProfile
  } = posterOptions;

  const nextAiringHeight = 19;

  const heights = [
    posterHeight,
    detailedProgressBar ? detailedProgressBarHeight : progressBarHeight,
    nextAiringHeight,
    isSmallScreen ? columnPaddingSmallScreen : columnPadding
  ];

  if (showTitle) {
    heights.push(19);
  }

  if (showAuthor) {
    heights.push(19);
  }

  if (showMonitored) {
    heights.push(19);
  }

  if (showQualityProfile) {
    heights.push(19);
  }

  switch (sortKey) {
    case 'seasons':
    case 'previousAiring':
    case 'added':
    case 'path':
    case 'sizeOnDisk':
      heights.push(19);
      break;
    case 'qualityProfileId':
      if (!showQualityProfile) {
        heights.push(19);
      }
      break;
    default:
      // No need to add a height of 0
  }

  return heights.reduce((acc, height) => acc + height, 0);
}

function calculatePosterHeight(posterWidth) {
  return Math.ceil((400 / 256) * posterWidth);
}

class BookIndexPostersInfinite extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      width: 0,
      columnWidth: 182,
      columnCount: 1,
      posterWidth: 162,
      posterHeight: 253,
      rowHeight: calculateRowHeight(253, null, props.isSmallScreen, {}),
      scrollRestored: false
    };

    this._grid = null;
    this._padding = props.isSmallScreen ? columnPaddingSmallScreen : columnPadding;
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      items,
      sortKey,
      posterOptions,
      isSmallScreen,
      isEditorActive,
      scrollTop,
      selectedState,
      scrollToIndex
    } = this.props;

    const {
      width,
      columnWidth,
      columnCount,
      rowHeight,
      scrollRestored
    } = this.state;

    if (prevProps.sortKey !== sortKey ||
        prevProps.posterOptions !== posterOptions) {
      this.calculateGrid(width, isSmallScreen);
    }

    if (this._grid &&
        (prevState.width !== width ||
            prevState.columnWidth !== columnWidth ||
            prevState.columnCount !== columnCount ||
            prevState.rowHeight !== rowHeight ||
            hasDifferentItemsOrOrder(prevProps.items, items) ||
            prevProps.isEditorActive !== isEditorActive ||
            prevProps.selectedState !== selectedState)) {
      // recomputeGridSize also forces Grid to discard its cache of rendered cells
      this._grid.recomputeGridSize();
    }

    if (this._grid && scrollTop !== 0 && !scrollRestored) {
      this.setState({ scrollRestored: true });
      this._grid.scrollToPosition({ scrollTop });
    }

    if (this._grid && scrollToIndex != null && scrollToIndex !== prevProps.scrollToIndex) {
      this._grid.scrollToCell({
        rowIndex: Math.floor(scrollToIndex / columnCount),
        columnIndex: 0
      });
    }
  }

  //
  // Control

  setGridRef = (ref) => {
    this._grid = ref;
  };

  calculateGrid = (width = this.state.width, isSmallScreen) => {
    const {
      sortKey,
      posterOptions
    } = this.props;

    const columnWidth = calculateColumnWidth(width, posterOptions.size, isSmallScreen);
    const columnCount = Math.max(Math.floor(width / columnWidth), 1);
    const posterWidth = columnWidth - this._padding * 2;
    const posterHeight = calculatePosterHeight(posterWidth);
    const rowHeight = calculateRowHeight(posterHeight, sortKey, isSmallScreen, posterOptions);

    this.setState({
      width,
      columnWidth,
      columnCount,
      posterWidth,
      posterHeight,
      rowHeight
    });
  };

  //
  // Grid callbacks

  cellRenderer = ({ key, rowIndex, columnIndex, style }) => {
    const {
      getBookAtIndex,
      sortKey,
      posterOptions,
      showRelativeDates,
      shortDateFormat,
      timeFormat,
      selectedState,
      isEditorActive,
      onSelectedChange
    } = this.props;

    const {
      posterWidth,
      posterHeight,
      columnCount
    } = this.state;

    const {
      detailedProgressBar,
      showTitle,
      showAuthor,
      showMonitored,
      showQualityProfile
    } = posterOptions;

    const book = getBookAtIndex(rowIndex * columnCount + columnIndex);

    if (!book) {
      // Placeholder while the page holding this poster loads.
      return (
        <div
          key={key}
          style={{
            ...style,
            padding: this._padding
          }}
        >
          <div
            className={styles.placeholder}
            style={{
              width: posterWidth,
              height: posterHeight
            }}
          />
        </div>
      );
    }

    return (
      <div
        key={key}
        style={{
          ...style,
          padding: this._padding
        }}
      >
        <BookIndexItemConnector
          key={book.id}
          component={BookIndexPoster}
          sortKey={sortKey}
          posterWidth={posterWidth}
          posterHeight={posterHeight}
          detailedProgressBar={detailedProgressBar}
          showTitle={showTitle}
          showAuthor={showAuthor}
          showMonitored={showMonitored}
          showQualityProfile={showQualityProfile}
          showRelativeDates={showRelativeDates}
          shortDateFormat={shortDateFormat}
          timeFormat={timeFormat}
          style={style}
          book={book}
          bookId={book.id}
          authorId={book.authorId}
          isSelected={selectedState[book.id]}
          onSelectedChange={onSelectedChange}
          isEditorActive={isEditorActive}
        />
      </div>
    );
  };

  //
  // Listeners

  onMeasure = ({ width }) => {
    this.calculateGrid(width, this.props.isSmallScreen);
  };

  onSectionRendered = ({ rowOverscanStartIndex, rowOverscanStopIndex }) => {
    const { columnCount } = this.state;

    // The grid renders rows of posters; the paged data is indexed by book.
    this.props.onRowsRendered({
      startIndex: rowOverscanStartIndex * columnCount,
      stopIndex: (rowOverscanStopIndex + 1) * columnCount - 1
    });
  };

  //
  // Render

  render() {
    const {
      scroller,
      isSmallScreen,
      rowCount: bookCount,
      dataVersion
    } = this.props;

    const {
      width,
      columnWidth,
      columnCount,
      rowHeight
    } = this.state;

    const rowCount = Math.ceil(bookCount / columnCount);

    return (
      <Measure onMeasure={this.onMeasure}>
        <WindowScroller scrollElement={isSmallScreen ? undefined : scroller}>
          {({ height, registerChild, onChildScroll, scrollTop }) => {
            if (!height) {
              return <div />;
            }

            return (
              <div ref={registerChild}>
                <Grid
                  ref={this.setGridRef}
                  className={styles.grid}
                  autoHeight={true}
                  height={height}
                  columnCount={columnCount}
                  columnWidth={columnWidth}
                  rowCount={rowCount}
                  rowHeight={rowHeight}
                  width={width}
                  onScroll={onChildScroll}
                  scrollTop={scrollTop}
                  overscanRowCount={3}
                  overscanColumnCount={1}
                  cellRenderer={this.cellRenderer}
                  onSectionRendered={this.onSectionRendered}
                  dataVersion={dataVersion}
                  scrollToAlignment={'start'}
                  isScrollingOptOut={true}
                />
              </div>
            );
          }}
        </WindowScroller>
      </Measure>
    );
  }
}

BookIndexPostersInfinite.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  posterOptions: PropTypes.object.isRequired,
  scrollTop: PropTypes.number.isRequired,
  scroller: PropTypes.instanceOf(Element).isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  timeFormat: PropTypes.string.isRequired,
  selectedState: PropTypes.object.isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,

  // From withInfiniteBooks
  rowCount: PropTypes.number.isRequired,
  getBookAtIndex: PropTypes.func.isRequired,
  dataVersion: PropTypes.number.isRequired,
  scrollToIndex: PropTypes.number,
  onRowsRendered: PropTypes.func.isRequired
};

export default BookIndexPostersInfinite;
