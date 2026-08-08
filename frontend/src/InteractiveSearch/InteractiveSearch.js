import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds, sortDirections } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import FilterWarning from './FilterWarning';
import InteractiveSearchMediaTypeToggle from './InteractiveSearchMediaTypeToggle';
import InteractiveSearchRow from './InteractiveSearchRow';
import styles from './InteractiveSearch.css';

const hiddenResultsPageSize = 50;

const columns = [
  {
    name: 'rank',
    label: 'Rank',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'protocol',
    label: 'Source',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'age',
    label: 'Age',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'title',
    label: 'Title',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'indexer',
    label: 'Indexer',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'size',
    label: 'Size',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'peers',
    label: 'Peers',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'duration',
    label: 'Duration',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'narrator',
    label: 'Narrator',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'quality',
    label: 'Quality',
    isSortable: true,
    isVisible: true
  },
  {
    name: 'customFormats',
    label: React.createElement(Icon, {
      name: icons.INTERACTIVE,
      title: 'Custom Format'
    }),
    isSortable: true,
    isVisible: true
  },
  {
    name: 'indexerFlags',
    label: React.createElement(Icon, {
      name: icons.FLAG,
      title: 'Indexer Flags'
    }),
    isSortable: true,
    isVisible: true
  },
  {
    name: 'rejections',
    label: React.createElement(Icon, {
      name: icons.DANGER,
      title: 'Rejections'
    }),
    isSortable: true,
    fixedSortDirection: sortDirections.ASCENDING,
    isVisible: true
  },
  {
    name: 'releaseWeight',
    label: React.createElement(Icon, { name: icons.DOWNLOAD }),
    isSortable: true,
    fixedSortDirection: sortDirections.ASCENDING,
    isVisible: true
  }
];

class InteractiveSearch extends Component {
  constructor(props) {
    super(props);

    this.state = {
      showHiddenResults: false,
      hiddenResultsPage: 0
    };
  }

  componentDidUpdate(prevProps) {
    const searchChanged =
      prevProps.searchPayload?.bookId !== this.props.searchPayload?.bookId ||
      prevProps.searchPayload?.authorId !== this.props.searchPayload?.authorId ||
      prevProps.searchPayload?.initialMediaType !== this.props.searchPayload?.initialMediaType;

    if (searchChanged || prevProps.hiddenItems.length !== this.props.hiddenItems.length) {
      if (this.state.showHiddenResults || this.state.hiddenResultsPage !== 0) {
        this.setState({
          showHiddenResults: false,
          hiddenResultsPage: 0
        });
      }
    }
  }

  onToggleHiddenResults = () => {
    this.setState((prevState) => ({
      showHiddenResults: !prevState.showHiddenResults,
      hiddenResultsPage: 0
    }));
  };

  onPreviousHiddenResultsPage = () => {
    this.setState((prevState) => ({
      hiddenResultsPage: Math.max(prevState.hiddenResultsPage - 1, 0)
    }));
  };

  onNextHiddenResultsPage = () => {
    const hiddenPageCount = Math.ceil(this.props.hiddenItems.length / hiddenResultsPageSize);

    this.setState((prevState) => ({
      hiddenResultsPage: Math.min(prevState.hiddenResultsPage + 1, Math.max(hiddenPageCount - 1, 0))
    }));
  };

  onMediaTypeChange = (mediaType) => {
    const {
      siblingBookId,
      siblingMediaType,
      siblingToggleEnabled,
      onMediaTypeChange
    } = this.props;

    if (!siblingToggleEnabled || mediaType !== siblingMediaType || siblingBookId == null || !onMediaTypeChange) {
      return;
    }

    // Switch to the sibling row, not just the media label. The backend search uses
    // that bookId to select the sibling's monitored edition title/profile.
    onMediaTypeChange({
      bookId: siblingBookId,
      mediaType: siblingMediaType
    });
  };

  renderTableRows(items, preferredIndex) {
    const {
      searchPayload,
      longDateFormat,
      timeFormat,
      onGrabPress
    } = this.props;

    return items.map((item, index) => (
      <InteractiveSearchRow
        key={`${item.indexerId}-${item.guid}`}
        {...item}
        isPreferredChoice={preferredIndex !== -1 && index === preferredIndex}
        searchPayload={searchPayload}
        longDateFormat={longDateFormat}
        timeFormat={timeFormat}
        onGrabPress={onGrabPress}
      />
    ));
  }

  render() {
    const {
      searchPayload,
      isFetching,
      isPopulated,
      error,
      totalReleasesCount,
      items,
      hiddenItems,
      sortKey,
      sortDirection,
      bypassFilters,
      filterSummary,
      siblingMediaType,
      siblingToggleEnabled,
      siblingToggleDisabledReason,
      onSortPress,
      onToggleBypass
    } = this.props;

    const { showHiddenResults } = this.state;
    const selectedMediaType = searchPayload?.initialMediaType === 'ebook' ? 'ebook' : 'audiobook';
    const preferredIndex = items.findIndex((item) => item.approved && item.downloadAllowed);
    const hiddenCount = hiddenItems.length;
    const hiddenPageCount = Math.ceil(hiddenCount / hiddenResultsPageSize);
    const hiddenResultsPage = Math.min(this.state.hiddenResultsPage, Math.max(hiddenPageCount - 1, 0));
    const hiddenRangeStart = hiddenResultsPage * hiddenResultsPageSize;
    const hiddenRangeEnd = Math.min(hiddenRangeStart + hiddenResultsPageSize, hiddenCount);
    const pagedHiddenItems = hiddenItems.slice(hiddenRangeStart, hiddenRangeEnd);
    const shouldShowToggle = !error && searchPayload?.bookId != null;

    return (
      <div>
        {shouldShowToggle ?
          <InteractiveSearchMediaTypeToggle
            selectedMediaType={selectedMediaType}
            siblingMediaType={siblingMediaType}
            siblingToggleEnabled={siblingToggleEnabled}
            siblingToggleDisabledReason={siblingToggleDisabledReason}
            onMediaTypeChange={this.onMediaTypeChange}
          /> :
          null}

        {isFetching ? <LoadingIndicator /> : null}

        {!isFetching && error ?
          <div className={styles.blankpad}>
            {translate('InteractiveSearchUnableToLoadResults')}
          </div> :
          null}

        {!isFetching && isPopulated && !totalReleasesCount && (!filterSummary || !filterSummary.hasSoftFilters) ?
          <Alert kind={kinds.INFO}>
            {translate('NoResultsFound')}
          </Alert> :
          null}

        {!!totalReleasesCount && isPopulated && !items.length && !hiddenItems.length && filterSummary && filterSummary.hasSoftFilters ?
          <FilterWarning
            filterSummary={filterSummary}
            bypassFilters={bypassFilters}
            onToggleBypass={onToggleBypass}
          /> :
          null}

        {!!totalReleasesCount && isPopulated && !items.length && !hiddenItems.length && (!filterSummary || !filterSummary.hasSoftFilters) ?
          <Alert kind={kinds.WARNING}>
            <div>
              {translate('AllResultsAreHiddenByTheAppliedFilter')}
              {filterSummary && filterSummary.filterWarnings && filterSummary.filterWarnings.length > 0 && (
                <div style={{ marginTop: '10px' }}>
                  <strong>{translate('InteractiveSearchReasonsLabel')}</strong>
                  <ul style={{ marginTop: '5px', marginBottom: 0 }}>
                    {filterSummary.filterWarnings.map((warning, index) => (
                      <li key={index}>{warning}</li>
                    ))}
                  </ul>
                </div>
              )}
              {filterSummary && filterSummary.filterBreakdown && filterSummary.filterBreakdown.quality > 0 && (
                <div style={{ marginTop: '10px', fontSize: '0.9em' }}>
                  <em>{translate('InteractiveSearchQualityProfileTip')}</em>
                </div>
              )}
            </div>
          </Alert> :
          null}

        {isPopulated && (items.length || hiddenItems.length) ?
          <div>
            {hiddenCount > 0 ?
              <div style={{ marginBottom: '12px' }}>
                <Button
                  kind={kinds.DEFAULT}
                  size="small"
                  onPress={this.onToggleHiddenResults}
                >
                  {showHiddenResults ? `Hide ${hiddenCount} Hidden Results` : `Show ${hiddenCount} Hidden Results`}
                </Button>
              </div> :
              null}

            {items.length ?
              <Table
                className={styles.table}
                columns={columns}
                sortKey={sortKey}
                sortDirection={sortDirection}
                onSortPress={onSortPress}
              >
                <TableBody>
                  {this.renderTableRows(items, preferredIndex)}
                </TableBody>
              </Table> :
              null}

            {showHiddenResults && hiddenItems.length ?
              <div style={{ marginTop: '24px' }}>
                <div className={styles.hiddenResultsHeader}>
                  <h3 className={styles.hiddenResultsTitle}>{translate('InteractiveSearchHiddenResultsTitle', { count: hiddenCount })}</h3>

                  <div className={styles.hiddenResultsPager}>
                    <span className={styles.hiddenResultsRange}>
                      {translate('InteractiveSearchHiddenResultsRange', { start: hiddenRangeStart + 1, end: hiddenRangeEnd, total: hiddenCount })}
                    </span>

                    <Button
                      kind={kinds.DEFAULT}
                      size="small"
                      isDisabled={hiddenResultsPage === 0}
                      onPress={this.onPreviousHiddenResultsPage}
                    >
                      {translate('Previous')}
                    </Button>

                    <Button
                      kind={kinds.DEFAULT}
                      size="small"
                      isDisabled={hiddenResultsPage >= hiddenPageCount - 1}
                      onPress={this.onNextHiddenResultsPage}
                    >
                      {translate('Next')}
                    </Button>
                  </div>
                </div>

                <Table
                  className={styles.table}
                  columns={columns}
                  sortKey={sortKey}
                  sortDirection={sortDirection}
                  onSortPress={onSortPress}
                >
                  <TableBody>
                    {this.renderTableRows(pagedHiddenItems, -1)}
                  </TableBody>
                </Table>
              </div> :
              null}
          </div> :
          null}
      </div>
    );
  }
}

InteractiveSearch.propTypes = {
  searchPayload: PropTypes.object.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  totalReleasesCount: PropTypes.number.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  hiddenItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  sortDirection: PropTypes.string,
  type: PropTypes.string.isRequired,
  longDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  bypassFilters: PropTypes.bool,
  filterSummary: PropTypes.object,
  siblingBookId: PropTypes.number,
  siblingMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  siblingToggleEnabled: PropTypes.bool,
  siblingToggleDisabledReason: PropTypes.string,
  onSortPress: PropTypes.func.isRequired,
  onGrabPress: PropTypes.func.isRequired,
  onToggleBypass: PropTypes.func,
  onMediaTypeChange: PropTypes.func
};

export default InteractiveSearch;
