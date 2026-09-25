import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import * as releaseActions from 'Store/Actions/releaseActions';
import createCustomFiltersSelector from 'Store/Selectors/createCustomFiltersSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import filterCollection from 'Utilities/Array/filterCollection';
import sortCollection from 'Utilities/Array/sortCollection';
import generateUUIDv4 from 'Utilities/String/generateUUIDv4';
import translate from 'Utilities/String/translate';
import InteractiveSearch from './InteractiveSearch';
import InteractiveSearchRow from './InteractiveSearchRow';
import styles from './InteractiveSearch.css';
import rowStyles from './InteractiveSearchRow.css';

function createMapStateToProps() {
  const selectAuthorFilterState = createSelector(
    (state) => state.releases,
    createCustomFiltersSelector('releases', 'releases.author'),
    (releaseState, customFilters) => ({
      ...releaseState,
      ...releaseState.author,
      customFilters
    })
  );

  return createSelector(
    (state) => state.releases.authorSearch,
    selectAuthorFilterState,
    createUISettingsSelector(),
    (authorSearch, filterState, uiSettings) => ({
      authorSearch,
      filterState,
      sortKey: filterState.sortKey,
      sortDirection: filterState.sortDirection,
      sortPredicates: filterState.sortPredicates,
      longDateFormat: uiSettings.longDateFormat,
      timeFormat: uiSettings.timeFormat
    })
  );
}

function createMapDispatchToProps(dispatch) {
  return {
    dispatchFetchAuthorReleases(payload) {
      dispatch(releaseActions.fetchAuthorReleases(payload));
    },

    dispatchCancelAuthorReleases(payload) {
      dispatch(releaseActions.cancelAuthorReleases(payload));
    },

    dispatchClearAuthorSearch(payload) {
      dispatch(releaseActions.clearAuthorSearch(payload));
    },

    dispatchToggleAuthorSearchBook(payload) {
      dispatch(releaseActions.toggleAuthorSearchBook(payload));
    },

    onSortPress(sortKey, sortDirection) {
      dispatch(releaseActions.setReleasesSort({ sortKey, sortDirection }));
    },

    onGrabPress(payload) {
      dispatch(releaseActions.grabRelease(payload));
    }
  };
}

function isPreferredRelease(release) {
  return release.isPreferredChoice === true &&
    release.downloadAllowed === true &&
    !(Array.isArray(release.rejections) && release.rejections.length);
}

function getGroupStatusText(bookResult, authorSearch) {
  if (bookResult.status === 'failed') {
    return translate('AuthorSearchBookFailed');
  }

  if (bookResult.status === 'complete') {
    if (!(bookResult.releases || []).length && !(bookResult.hiddenReleases || []).length) {
      return translate('AuthorSearchNoResultsReturned');
    }

    if (!(bookResult.releases || []).some(isPreferredRelease)) {
      return translate('AuthorSearchNoPreferredRelease');
    }

    return null;
  }

  if (authorSearch.isCancelled) {
    return translate('AuthorSearchStopped');
  }

  if (authorSearch.error || authorSearch.isComplete || (!authorSearch.isFetching && !authorSearch.isComplete)) {
    return translate('AuthorSearchIncompleteStatus');
  }

  return translate('AuthorSearchWaiting');
}

const resultColumns = [
  { name: 'book', label: () => translate('Book'), isVisible: true },
  { name: 'title', label: () => translate('Title'), isVisible: true },
  { name: 'indexer', label: () => translate('Indexer'), isVisible: true },
  { name: 'quality', label: () => translate('Quality'), isVisible: true },
  { name: 'size', label: () => translate('Size'), isVisible: true },
  { name: 'narrator', label: () => translate('Narrator'), isVisible: true },
  { name: 'download', label: () => translate('Download'), isVisible: true }
];

class AuthorInteractiveSearchTable extends Component {
  state = {
    isConfirmOpen: false
  };

  componentWillUnmount() {
    const { authorSearch, authorId, selectedMediaType } = this.props;

    if (
      authorSearch.searchId &&
      authorSearch.authorId === authorId &&
      authorSearch.mediaType === selectedMediaType
    ) {
      const scope = {
        searchId: authorSearch.searchId,
        authorId,
        mediaType: selectedMediaType
      };

      this.props.dispatchCancelAuthorReleases({ searchId: authorSearch.searchId });
      this.props.dispatchClearAuthorSearch(scope);
    }
  }

  onSearchPress = () => {
    if (this.props.searchCount > 5) {
      this.setState({ isConfirmOpen: true });
      return;
    }

    this.startSearch();
  };

  onSearchConfirmed = () => {
    this.setState({ isConfirmOpen: false });
    this.startSearch();
  };

  onSearchCancelled = () => {
    this.setState({ isConfirmOpen: false });
  };

  startSearch = () => {
    const { authorId, selectedMediaType, dispatchFetchAuthorReleases } = this.props;

    if (!authorId || !selectedMediaType) {
      return;
    }

    dispatchFetchAuthorReleases({
      authorId,
      mediaType: selectedMediaType,
      searchId: generateUUIDv4()
    });
  };

  onToggleBook = (bookId) => {
    this.props.dispatchToggleAuthorSearchBook({
      searchId: this.props.authorSearch.searchId,
      bookId
    });
  };

  renderBookResult(bookResult) {
    const {
      authorSearch,
      sortKey,
      sortDirection,
      sortPredicates,
      filterState,
      longDateFormat,
      timeFormat,
      onSortPress,
      onGrabPress
    } = this.props;
    const isExpanded = authorSearch.expandedBookIds[bookResult.bookId] === true;
    const releases = bookResult.releases || [];
    const hiddenReleases = bookResult.hiddenReleases || [];
    const preferredRelease = releases.find(isPreferredRelease);
    const otherReleases = preferredRelease ? releases.filter((release) => release !== preferredRelease) : releases;
    const statusText = getGroupStatusText(bookResult, authorSearch);
    const searchPayload = {
      authorId: authorSearch.authorId,
      mediaType: authorSearch.mediaType,
      searchId: authorSearch.searchId,
      searchedBookId: bookResult.bookId
    };
    const hasOtherResults = otherReleases.length > 0 || hiddenReleases.length > 0;
    const sortOptions = {
      ...filterState,
      sortKey,
      sortDirection,
      sortPredicates,
      selectedMediaType: authorSearch.mediaType
    };
    const sortedReleases = isExpanded ? sortCollection(filterCollection(otherReleases, filterState), sortOptions) : [];
    const sortedHiddenReleases = isExpanded ? sortCollection(hiddenReleases, sortOptions) : [];
    const bookControl = (
      <Button
        className={styles.authorSearchBookButton}
        kind={kinds.DEFAULT}
        size="small"
        aria-label={translate(isExpanded ? 'AuthorSearchCollapseBook' : 'AuthorSearchExpandBook', { title: bookResult.title })}
        aria-expanded={isExpanded}
        title={translate(isExpanded ? 'AuthorSearchCollapseBook' : 'AuthorSearchExpandBook', { title: bookResult.title })}
        onPress={() => this.onToggleBook(bookResult.bookId)}
      >
        <span className={styles.authorSearchBookTitle}>{bookResult.title}</span>
        <span className={styles.authorSearchBookDisclosure}>
          <Icon
            name={isExpanded ? icons.COLLAPSE : icons.EXPAND}
            size={16}
          />
          <span>{translate(isExpanded ? 'Collapse' : 'Expand')}</span>
        </span>
      </Button>
    );
    let expandedContent = null;

    if (hasOtherResults) {
      expandedContent = (
        <InteractiveSearch
          searchPayload={{ ...searchPayload, initialMediaType: authorSearch.mediaType }}
          type="author"
          isFetching={bookResult.status === 'pending' && authorSearch.isFetching}
          isPopulated={bookResult.status !== 'pending' || authorSearch.isComplete || !authorSearch.isFetching}
          error={bookResult.status === 'failed' ? { message: translate('AuthorSearchBookFailed') } : null}
          totalReleasesCount={otherReleases.length + hiddenReleases.length}
          items={sortedReleases}
          hiddenItems={sortedHiddenReleases}
          sortKey={sortKey}
          sortDirection={sortDirection}
          bypassFilters={false}
          longDateFormat={longDateFormat}
          timeFormat={timeFormat}
          onSortPress={onSortPress}
          onGrabPress={onGrabPress}
        />
      );
    } else if (preferredRelease) {
      expandedContent = <div className={styles.authorSearchExpandedEmpty}>{translate('AuthorSearchNoOtherResults')}</div>;
    }

    const expandedRow = isExpanded ? (
      <TableRow className={styles.authorSearchExpandedRow}>
        <TableRowCell className={styles.authorSearchExpandedCell} colSpan={7}>
          {expandedContent}
        </TableRowCell>
      </TableRow>
    ) : null;

    return (
      <React.Fragment key={bookResult.bookId}>
        {preferredRelease ?
          <InteractiveSearchRow
            {...preferredRelease}
            summaryMode={true}
            summaryBookLabel={bookControl}
            summaryStatusText={statusText}
            isPreferredChoice={true}
            searchPayload={searchPayload}
            longDateFormat={longDateFormat}
            timeFormat={timeFormat}
            onGrabPress={onGrabPress}
          /> :
          <TableRow>
            <TableRowCell className={rowStyles.summaryBook}>{bookControl}</TableRowCell>
            <TableRowCell className={styles.authorSearchStatusCell}>{statusText}</TableRowCell>
            <TableRowCell />
            <TableRowCell />
            <TableRowCell />
            <TableRowCell />
            <TableRowCell />
          </TableRow>}
        {expandedRow}
      </React.Fragment>
    );
  }

  render() {
    const {
      selectedMediaType,
      searchCount,
      authorSearch
    } = this.props;
    const isSearchDisabled = !searchCount || !selectedMediaType || authorSearch.isFetching;

    return (
      <div>
        <p>{translate('SearchAuthorBooksHelp', { count: searchCount })}</p>
        <Button
          isDisabled={isSearchDisabled}
          onPress={this.onSearchPress}
        >
          {translate('Search')}
        </Button>

        <ConfirmModal
          isOpen={this.state.isConfirmOpen}
          title={translate('InteractiveSearch')}
          message={translate('SearchAuthorBooksConfirm', { count: searchCount })}
          confirmLabel={translate('Search')}
          onConfirm={this.onSearchConfirmed}
          onCancel={this.onSearchCancelled}
        />

        {authorSearch.isFetching && !authorSearch.bookResults.length ? <LoadingIndicator /> : null}
        {authorSearch.error ?
          <Alert kind={kinds.WARNING}>
            {translate('AuthorSearchIncomplete')}
          </Alert> :
          null}
        {authorSearch.isCancelled ?
          <Alert kind={kinds.WARNING}>
            {translate('AuthorSearchStopped')}
          </Alert> :
          null}
        {!authorSearch.isFetching && authorSearch.isComplete && !authorSearch.isCancelled && !authorSearch.bookResults.length ?
          <Alert kind={kinds.INFO}>
            {translate('AuthorSearchNoBooksReturned')}
          </Alert> :
          null}

        {authorSearch.bookResults.length ?
          <Table className={styles.summaryTable} columns={resultColumns}>
            <TableBody>
              {authorSearch.bookResults.map((bookResult) => this.renderBookResult(bookResult))}
            </TableBody>
          </Table> :
          null}
      </div>
    );
  }
}

AuthorInteractiveSearchTable.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  searchCount: PropTypes.number,
  authorSearch: PropTypes.object.isRequired,
  filterState: PropTypes.object.isRequired,
  sortKey: PropTypes.string.isRequired,
  sortDirection: PropTypes.string.isRequired,
  sortPredicates: PropTypes.object.isRequired,
  longDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  dispatchFetchAuthorReleases: PropTypes.func.isRequired,
  dispatchCancelAuthorReleases: PropTypes.func.isRequired,
  dispatchClearAuthorSearch: PropTypes.func.isRequired,
  dispatchToggleAuthorSearchBook: PropTypes.func.isRequired,
  onSortPress: PropTypes.func.isRequired,
  onGrabPress: PropTypes.func.isRequired
};

AuthorInteractiveSearchTable.defaultProps = {
  selectedMediaType: null,
  searchCount: 0
};

export default connect(createMapStateToProps, createMapDispatchToProps)(AuthorInteractiveSearchTable);
