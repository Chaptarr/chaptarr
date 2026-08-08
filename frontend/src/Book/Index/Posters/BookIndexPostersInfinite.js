import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { Grid, WindowScroller, InfiniteLoader } from 'react-virtualized';
import BookIndexItemConnector from 'Book/Index/BookIndexItemConnector';
import Measure from 'Components/Measure';
import dimensions from 'Styles/Variables/dimensions';
import getIndexOfFirstCharacter from 'Utilities/Array/getIndexOfFirstCharacter';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import * as bookInfiniteScrollActions from 'Store/Actions/bookInfiniteScrollActions';
import {
  selectQueryBooks,
  selectQueryBuckets,
  selectPageStatus,
  selectQueryHasMore
} from 'Store/Reducers/bookInfiniteScrollReducer';
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

    this._isInitialized = false;
    this._grid = null;
    this._infiniteLoaderRef = null;
    this._padding = props.isSmallScreen ? columnPaddingSmallScreen : columnPadding;
  }

  componentDidMount() {
    const { queryKey, queryParams, dispatchFetchBuckets, dispatchSetActiveQuery } = this.props;
    
    // Set this query as active with parameters
    dispatchSetActiveQuery({ queryKey, queryParams });
    
    // Fetch buckets for jump bar
    dispatchFetchBuckets({ queryKey });
    
    // Fetch initial pages
    this.loadInitialPages();
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      items,
      sortKey,
      posterOptions,
      jumpToCharacter,
      isSmallScreen,
      isEditorActive,
      scrollTop,
      selectedState,
      queryKey,
      queryParams,
      dispatchFetchBuckets,
      dispatchSetActiveQuery,
      dispatchInvalidateQuery
    } = this.props;

    const {
      width,
      columnWidth,
      columnCount,
      rowHeight,
      scrollRestored
    } = this.state;

    // Handle query changes
    if (prevProps.queryKey !== queryKey) {
      // Invalidate old query and set new one as active
      if (prevProps.queryKey) {
        dispatchInvalidateQuery(prevProps.queryKey);
      }
      dispatchSetActiveQuery({ queryKey, queryParams });
      dispatchFetchBuckets({ queryKey });
      this.loadInitialPages();
    }

    if (prevProps.sortKey !== sortKey ||
        prevProps.posterOptions !== posterOptions) {
      this.calculateGrid(width, isSmallScreen);
    }

    if (this._grid &&
        ((prevState.width !== width ||
            prevState.columnWidth !== columnWidth ||
            prevState.columnCount !== columnCount ||
            prevState.rowHeight !== rowHeight ||
            hasDifferentItemsOrOrder(prevProps.items, items)) ||
            prevProps.isEditorActive !== isEditorActive ||
            prevProps.selectedState !== selectedState)) {
      // recomputeGridSize also forces Grid to discard its cache of rendered cells
      this._grid.recomputeGridSize();
      
      // Reset InfiniteLoader cache when column count changes
      if (prevState.columnCount !== columnCount && this._infiniteLoaderRef) {
        this._infiniteLoaderRef.resetLoadMoreRowsCache();
      }
    }

    if (this._grid && scrollTop !== 0 && !scrollRestored) {
      this.setState({ scrollRestored: true });
      this._grid.scrollToPosition({ scrollTop });
    }

    // Handle jump to character
    if (jumpToCharacter != null && jumpToCharacter !== prevProps.jumpToCharacter) {
      this.handleJumpToCharacter(jumpToCharacter);
    }
  }

  componentWillUnmount() {
    const { dispatchAbortAllRequests } = this.props;
    // Abort any in-flight requests
    dispatchAbortAllRequests();
  }

  //
  // Control

  setGridRef = (ref) => {
    this._grid = ref;
  };

  setInfiniteLoaderRef = (ref) => {
    this._infiniteLoaderRef = ref;
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

  loadInitialPages = () => {
    const { queryKey, pageSize, dispatchFetchBooksPage } = this.props;
    
    // Load first 2 pages initially
    this.props.dispatchFetchBooksPage({ queryKey, pageIndex: 0 });
    this.props.dispatchFetchBooksPage({ queryKey, pageIndex: 1 });
  };

  handleJumpToCharacter = (letter) => {
    const { 
      queryKey, 
      buckets, 
      pageSize,
      dispatchFetchBooksPage,
      dispatchJumpToLetter
    } = this.props;
    
    const { columnCount } = this.state;
    
    const bucketsReady = buckets &&
      buckets.status === 'succeeded' &&
      buckets.cumulativeIndexes &&
      Object.keys(buckets.cumulativeIndexes).length > 0;

    if (!bucketsReady) {
      // Buckets not loaded yet, fetch them first
      dispatchJumpToLetter({ queryKey, letter }).then(({ targetIndex }) => {
        if (targetIndex !== undefined && this._grid) {
          const rowIndex = Math.floor(targetIndex / columnCount);

          // Prefetch the pages that cover the target row (handles row spanning two pages)
          const rowStartIndex = rowIndex * columnCount;
          const rowEndIndex = rowStartIndex + columnCount - 1;
          const startPage = Math.floor(rowStartIndex / pageSize);
          const endPage = Math.floor(rowEndIndex / pageSize);
          for (let pageIndex = startPage; pageIndex <= endPage; pageIndex++) {
            dispatchFetchBooksPage({ queryKey, pageIndex });
          }

          this._grid.scrollToCell({
            rowIndex,
            columnIndex: 0
          });
        }
      });
    } else {
      const targetIndex = buckets.cumulativeIndexes[letter] || 0;
      const rowIndex = Math.floor(targetIndex / columnCount);
      
      // Prefetch the pages that cover the target row (handles row spanning two pages)
      const rowStartIndex = rowIndex * columnCount;
      const rowEndIndex = rowStartIndex + columnCount - 1;
      const startPage = Math.floor(rowStartIndex / pageSize);
      const endPage = Math.floor(rowEndIndex / pageSize);
      for (let pageIndex = startPage; pageIndex <= endPage; pageIndex++) {
        dispatchFetchBooksPage({ queryKey, pageIndex });
      }
      
      // Scroll immediately
      if (this._grid) {
        this._grid.scrollToCell({
          rowIndex,
          columnIndex: 0
        });
      }
    }
  };

  //
  // InfiniteLoader callbacks

  isRowLoaded = ({ index }) => {
    const { queryKey, pageSize, getPageStatus } = this.props;
    const { columnCount } = this.state;
    
    // Convert row index to item range
    const startItemIndex = index * columnCount;
    const endItemIndex = startItemIndex + columnCount - 1;
    
    // Check if all items in this row are loaded
    for (let itemIndex = startItemIndex; itemIndex <= endItemIndex; itemIndex++) {
      const pageIndex = Math.floor(itemIndex / pageSize);
      const status = getPageStatus(queryKey, pageIndex);
      
      // If any item in the row is not loaded, the row is not loaded
      if (status !== 'succeeded' && status !== 'loading') {
        return false;
      }
    }
    
    return true;
  };

  loadMoreRows = ({ startIndex, stopIndex }) => {
    const { queryKey, pageSize, dispatchFetchBooksPage, getPageStatus } = this.props;
    const { columnCount } = this.state;
    
    // Convert row indices to item indices
    const startItemIndex = startIndex * columnCount;
    const endItemIndex = (stopIndex + 1) * columnCount - 1;
    
    // Convert to page indices
    const startPage = Math.floor(startItemIndex / pageSize);
    const endPage = Math.floor(endItemIndex / pageSize);
    
    const promises = [];
    
    for (let pageIndex = startPage; pageIndex <= endPage; pageIndex++) {
      const status = getPageStatus(queryKey, pageIndex);
      
      // Only fetch if not already loaded or loading
      if (status !== 'succeeded' && status !== 'loading') {
        promises.push(this.props.dispatchFetchBooksPage({ queryKey, pageIndex }));
      }
    }
    
    // Return resolved promise if nothing to load
    return promises.length > 0 ? Promise.all(promises) : Promise.resolve();
  };

  //
  // Grid callbacks

  cellRenderer = ({ key, rowIndex, columnIndex, style }) => {
    const {
      items,
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

    const bookIdx = rowIndex * columnCount + columnIndex;
    const book = getBookAtIndex ? getBookAtIndex(bookIdx) : items[bookIdx];

    if (!book) {
      // Return placeholder for unloaded items
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
          book={book}  // Pass full book object
          bookId={book.id}  // Keep for backward compatibility
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

  //
  // Render

  render() {
    const {
      scroller,
      items,
      isSmallScreen,
      totalCount,
      queryKey,
      pageSize
    } = this.props;

    const {
      width,
      columnWidth,
      columnCount,
      rowHeight
    } = this.state;

    // Calculate item count and row count
    const itemCount = totalCount ?? (items.length || 1000); // Use large fallback if unknown
    const rowCount = Math.ceil(itemCount / columnCount);
    
    // Threshold for prefetching (3 rows ahead)
    const threshold = columnCount * 3;

    return (
      <Measure onMeasure={this.onMeasure}>
        <InfiniteLoader
          ref={this.setInfiniteLoaderRef}
          key={`il-${queryKey}-${columnCount}`} // Reset on query or column change
          isRowLoaded={this.isRowLoaded}
          loadMoreRows={this.loadMoreRows}
          rowCount={rowCount} // InfiniteLoader works with rows, not items
          minimumBatchSize={10} // Minimum rows to load at once
          threshold={3} // Load 3 rows ahead
        >
          {({ onRowsRendered, registerChild }) => (
            <WindowScroller scrollElement={isSmallScreen ? undefined : scroller}>
              {({ height, registerChild: registerWindowChild, onChildScroll, scrollTop }) => {
                if (!height) {
                  return <div />;
                }

                return (
                  <div ref={registerWindowChild}>
                    <Grid
                      ref={(ref) => {
                        this.setGridRef(ref);
                        registerChild(ref); // Register with InfiniteLoader
                      }}
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
                      overscanRowCount={3} // Increased from 2 for smoother scrolling
                      overscanColumnCount={1}
                      cellRenderer={this.cellRenderer}
                      onSectionRendered={({ rowStartIndex, rowStopIndex }) => {
                        // InfiniteLoader expects row indices
                        onRowsRendered({ startIndex: rowStartIndex, stopIndex: rowStopIndex });
                      }}
                      scrollToAlignment={'start'}
                      isScrollingOptOut={true}
                    />
                  </div>
                );
              }}
            </WindowScroller>
          )}
        </InfiniteLoader>
      </Measure>
    );
  }
}

BookIndexPostersInfinite.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  posterOptions: PropTypes.object.isRequired,
  jumpToCharacter: PropTypes.string,
  scrollTop: PropTypes.number.isRequired,
  scroller: PropTypes.instanceOf(Element).isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  timeFormat: PropTypes.string.isRequired,
  selectedState: PropTypes.object.isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  
  // From infinite scroll
  queryKey: PropTypes.string.isRequired,
  queryParams: PropTypes.object.isRequired,
  totalCount: PropTypes.number,
  buckets: PropTypes.object,
  pageSize: PropTypes.number.isRequired,
  getBookAtIndex: PropTypes.func,
  getPageStatus: PropTypes.func.isRequired,
  
  // Actions
  dispatchFetchBooksPage: PropTypes.func.isRequired,
  dispatchFetchBuckets: PropTypes.func.isRequired,
  dispatchSetActiveQuery: PropTypes.func.isRequired,
  dispatchInvalidateQuery: PropTypes.func.isRequired,
  dispatchJumpToLetter: PropTypes.func.isRequired,
  dispatchAbortAllRequests: PropTypes.func.isRequired
};

BookIndexPostersInfinite.defaultProps = {
  pageSize: 200
};

//
// Connected Component

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookIndex,
    (state) => state.bookInfiniteScroll,
    createUISettingsSelector(),
    createDimensionsSelector(),
    (bookIndex, bookInfiniteScroll, uiSettings, dimensions) => {
      // Generate query key from current filters/sort
      const sortKey = bookIndex.sortKey || 'cleanTitle';
      const sortDirection = bookIndex.sortDirection || 'ascending';
      const filterKey = bookIndex.selectedFilterKey || 'all';
      
      // Create a stable key for the query parameters
      const queryKey = `${sortKey}_${sortDirection}_${filterKey}`;
      
      const query = bookInfiniteScroll.queries[queryKey] || {};
      
      return {
        // From bookIndex
        posterOptions: bookIndex.posterOptions,
        sortKey: bookIndex.sortKey,
        
        // From infinite scroll
        queryKey,
        items: selectQueryBooks({ bookInfiniteScroll }, queryKey),
        totalCount: query.totalCount,
        buckets: query.buckets,
        pageSize: bookInfiniteScroll.pageSize,
        getPageStatus: (qKey, pageIndex) => selectPageStatus({ bookInfiniteScroll }, qKey, pageIndex),
        
        // UI settings
        showRelativeDates: uiSettings.showRelativeDates,
        shortDateFormat: uiSettings.shortDateFormat,
        timeFormat: uiSettings.timeFormat,
        isSmallScreen: dimensions.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchBooksPage: bookInfiniteScrollActions.fetchBooksPage,
  dispatchFetchBuckets: bookInfiniteScrollActions.fetchBookBuckets,
  dispatchSetActiveQuery: bookInfiniteScrollActions.setActiveQuery,
  dispatchInvalidateQuery: bookInfiniteScrollActions.invalidateQuery,
  dispatchJumpToLetter: bookInfiniteScrollActions.jumpToLetter,
  dispatchAbortAllRequests: bookInfiniteScrollActions.abortAllRequests
};

// Export raw component for external connectors
export { BookIndexPostersInfinite };

// Export connected version as default
export default connect(createMapStateToProps, mapDispatchToProps)(BookIndexPostersInfinite);
