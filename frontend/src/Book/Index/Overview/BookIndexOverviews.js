import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { Grid, WindowScroller } from 'react-virtualized';
import BookIndexItemConnector from 'Book/Index/BookIndexItemConnector';
import Measure from 'Components/Measure';
import dimensions from 'Styles/Variables/dimensions';
import getIndexOfFirstCharacter from 'Utilities/Array/getIndexOfFirstCharacter';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import BookIndexOverview from './BookIndexOverview';
import styles from './BookIndexOverviews.css';

// Poster container dimensions
const columnPadding = parseInt(dimensions.authorIndexColumnPadding);
const columnPaddingSmallScreen = parseInt(dimensions.authorIndexColumnPaddingSmallScreen);
const progressBarHeight = parseInt(dimensions.progressBarSmallHeight);
const detailedProgressBarHeight = parseInt(dimensions.progressBarMediumHeight);

function calculatePosterWidth(posterSize, isSmallScreen) {
  const maxiumPosterWidth = isSmallScreen ? 192 : 202;

  if (posterSize === 'large') {
    return maxiumPosterWidth;
  }

  if (posterSize === 'medium') {
    return Math.floor(maxiumPosterWidth * 0.75);
  }

  return Math.floor(maxiumPosterWidth * 0.5);
}

function calculateRowHeight(posterHeight, sortKey, isSmallScreen, overviewOptions) {
  const {
    detailedProgressBar
  } = overviewOptions;

  const heights = [
    posterHeight,
    detailedProgressBar ? detailedProgressBarHeight : progressBarHeight,
    isSmallScreen ? columnPaddingSmallScreen : columnPadding
  ];

  return heights.reduce((acc, height) => acc + height, 0);
}

function calculatePosterHeight(posterWidth) {
  return Math.ceil((400 / 256) * posterWidth);
}

class BookIndexOverviews extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      width: 0,
      columnCount: 1,
      posterWidth: 162,
      posterHeight: 253,
      rowHeight: calculateRowHeight(253, null, props.isSmallScreen, {}),
      scrollRestored: false
    };

    this._grid = null;
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      items,
      sortKey,
      overviewOptions,
      jumpToCharacter,
      scrollTop,
      isEditorActive,
      selectedState,
      scrollToIndex
    } = this.props;

    const {
      width,
      rowHeight,
      scrollRestored
    } = this.state;

    if (prevProps.sortKey !== sortKey ||
        prevProps.overviewOptions !== overviewOptions) {
      this.calculateGrid();
    }

    if (this._grid &&
        (prevState.width !== width ||
            prevState.rowHeight !== rowHeight ||
            hasDifferentItemsOrOrder(prevProps.items, items) ||
            prevProps.isEditorActive !== isEditorActive ||
            prevProps.selectedState !== selectedState ||
            prevProps.overviewOptions !== overviewOptions)) {
      // recomputeGridSize also forces Grid to discard its cache of rendered cells
      this._grid.recomputeGridSize();
    }

    if (this._grid && scrollTop !== 0 && !scrollRestored) {
      this.setState({ scrollRestored: true });
      this._grid.scrollToPosition({ scrollTop });
    }

    // Paged lists resolve jump bar letters on the server and pass the row index in.
    if (this._grid && scrollToIndex != null && scrollToIndex !== prevProps.scrollToIndex) {
      this._grid.scrollToCell({
        rowIndex: scrollToIndex,
        columnIndex: 0
      });
    } else if (jumpToCharacter != null && jumpToCharacter !== prevProps.jumpToCharacter) {
      const index = getIndexOfFirstCharacter(items, sortKey, jumpToCharacter);

      if (this._grid && index != null) {

        this._grid.scrollToCell({
          rowIndex: index,
          columnIndex: 0
        });
      }
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
      overviewOptions
    } = this.props;

    const posterWidth = calculatePosterWidth(overviewOptions.size, isSmallScreen);
    const posterHeight = calculatePosterHeight(posterWidth);
    const rowHeight = calculateRowHeight(posterHeight, sortKey, isSmallScreen, overviewOptions);

    this.setState({
      width,
      posterWidth,
      posterHeight,
      rowHeight
    });
  };

  cellRenderer = ({ key, rowIndex, style }) => {
    const {
      items,
      sortKey,
      overviewOptions,
      showRelativeDates,
      shortDateFormat,
      longDateFormat,
      timeFormat,
      isSmallScreen,
      selectedState,
      isEditorActive,
      onSelectedChange,
      getBookAtIndex
    } = this.props;

    const {
      posterWidth,
      posterHeight,
      rowHeight
    } = this.state;

    const book = getBookAtIndex ? getBookAtIndex(rowIndex) : items[rowIndex];

    if (!book) {
      // Placeholder while the page holding this row loads.
      return (
        <div
          key={key}
          style={style}
        />
      );
    }

    return (
      <div
        key={key}
        style={style}
      >
        <BookIndexItemConnector
          key={book.id}
          component={BookIndexOverview}
          sortKey={sortKey}
          posterWidth={posterWidth}
          posterHeight={posterHeight}
          rowHeight={rowHeight}
          overviewOptions={overviewOptions}
          showRelativeDates={showRelativeDates}
          shortDateFormat={shortDateFormat}
          longDateFormat={longDateFormat}
          timeFormat={timeFormat}
          isSmallScreen={isSmallScreen}
          book={getBookAtIndex ? book : undefined}
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

  onSectionRendered = ({ rowOverscanStartIndex, rowOverscanStopIndex }) => {
    const { onRowsRendered } = this.props;

    if (onRowsRendered) {
      onRowsRendered({ startIndex: rowOverscanStartIndex, stopIndex: rowOverscanStopIndex });
    }
  };

  onMeasure = ({ width }) => {
    this.calculateGrid(width, this.props.isSmallScreen);
  };

  //
  // Render

  render() {
    const {
      items,
      isSmallScreen,
      scroller,
      rowCount,
      getBookAtIndex,
      dataVersion
    } = this.props;

    const {
      width,
      rowHeight
    } = this.state;

    return (
      <Measure
        onMeasure={this.onMeasure}
      >
        <WindowScroller
          scrollElement={isSmallScreen ? undefined : scroller}
        >
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
                  columnCount={1}
                  columnWidth={width}
                  rowCount={getBookAtIndex ? rowCount : items.length}
                  dataVersion={dataVersion}
                  rowHeight={rowHeight}
                  width={width}
                  onScroll={onChildScroll}
                  scrollTop={scrollTop}
                  overscanRowCount={2}
                  cellRenderer={this.cellRenderer}
                  onSectionRendered={this.onSectionRendered}
                  scrollToAlignment={'start'}
                  isScrollingOptOut={true}
                />
              </div>
            );
          }
          }
        </WindowScroller>
      </Measure>
    );
  }
}

BookIndexOverviews.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  overviewOptions: PropTypes.object.isRequired,
  scrollTop: PropTypes.number.isRequired,
  jumpToCharacter: PropTypes.string,
  scroller: PropTypes.instanceOf(Element).isRequired,
  showRelativeDates: PropTypes.bool.isRequired,
  shortDateFormat: PropTypes.string.isRequired,
  longDateFormat: PropTypes.string.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  timeFormat: PropTypes.string.isRequired,
  selectedState: PropTypes.object.isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  rowCount: PropTypes.number,
  getBookAtIndex: PropTypes.func,
  dataVersion: PropTypes.number,
  scrollToIndex: PropTypes.number,
  onRowsRendered: PropTypes.func
};

export default BookIndexOverviews;
