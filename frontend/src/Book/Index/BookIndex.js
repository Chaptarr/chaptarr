import reduce from 'lodash/reduce';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import MediaTypeToggle from 'Author/Details/MediaTypeToggle';
import NoAuthor from 'Author/NoAuthor';
import BookEditorFooter from 'Book/Editor/BookEditorFooter';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageJumpBar from 'Components/Page/PageJumpBar';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import { align, icons, kinds, sortDirections } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import BookIndexFooterConnector from './BookIndexFooterConnector';
import BookIndexFilterMenu from './Menus/BookIndexFilterMenu';
import BookIndexSortMenu from './Menus/BookIndexSortMenu';
import BookIndexViewMenu from './Menus/BookIndexViewMenu';
import BookIndexOverviewsConnector from './Overview/BookIndexOverviewsConnector';
import BookIndexOverviewsInfiniteConnector from './Overview/BookIndexOverviewsInfiniteConnector';
import BookIndexOverviewOptionsModal from './Overview/Options/BookIndexOverviewOptionsModal';
import BookIndexPostersConnector from './Posters/BookIndexPostersConnector';
import BookIndexPostersInfiniteConnector from './Posters/BookIndexPostersInfiniteConnector';
import BookIndexPosterOptionsModal from './Posters/Options/BookIndexPosterOptionsModal';
import BookIndexTableConnector from './Table/BookIndexTableConnector';
import BookIndexTableInfiniteConnector from './Table/BookIndexTableInfiniteConnector';
import BookIndexTableOptionsConnector from './Table/BookIndexTableOptionsConnector';
import styles from './BookIndex.css';

function getViewComponent(view, useClientSideBooks) {
  if (view === 'posters') {
    return useClientSideBooks ? BookIndexPostersConnector : BookIndexPostersInfiniteConnector;
  }

  if (view === 'overview') {
    return useClientSideBooks ? BookIndexOverviewsConnector : BookIndexOverviewsInfiniteConnector;
  }

  return useClientSideBooks ? BookIndexTableConnector : BookIndexTableInfiniteConnector;
}

function getBookIds(response) {
  const ids = Array.isArray(response) ? response : response?.ids;

  return Array.isArray(ids) ? ids : null;
}

class BookIndex extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      scroller: null,
      jumpBarItems: { order: [] },
      jumpToCharacter: null,
      isPosterOptionsModalOpen: false,
      isOverviewOptionsModalOpen: false,
      isConfirmSearchModalOpen: false,
      isEditorActive: false,
      isStartingRefresh: false,
      refreshError: null,
      failedRefreshBookIds: [],
      isStartingRssSync: false,
      rssSyncError: null,
      isSelectingAll: false,
      selectAllError: null,
      selectAllErrorKind: null,
      isResolvingSearchBookIds: false,
      isSubmittingBookSearch: false,
      searchBookIdsError: null,
      searchBookIdsErrorKind: null,
      searchBookIdsErrorHelp: null,
      searchScopeChangeNotice: null,
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {}
    };
  }

  componentDidMount() {
    this._isUnmounted = false;
    this.setJumpBarItems();
    this.setSelectedState();
  }

  componentDidUpdate(prevProps) {
    const {
      items,
      sortKey,
      sortDirection,
      view,
      isRefreshingBook,
      isRssSyncExecuting,
      selectedFilterKey,
      selectedMediaType,
      bookBuckets,
      useClientSideBooks
    } = this.props;

    const itemsChanged = hasDifferentItemsOrOrder(prevProps.items, items);
    const bucketsChanged = prevProps.bookBuckets !== bookBuckets;

    const sortChanged = sortKey !== prevProps.sortKey || sortDirection !== prevProps.sortDirection;
    const viewChanged = view !== prevProps.view;
    const selectionScopeChanged = viewChanged ||
      selectedFilterKey !== prevProps.selectedFilterKey ||
      selectedMediaType !== prevProps.selectedMediaType;

    if (this.state.isStartingRefresh && isRefreshingBook) {
      this.setState({ isStartingRefresh: false });
    }

    if (this.state.isStartingRssSync && isRssSyncExecuting) {
      this.setState({ isStartingRssSync: false });
    }

    const bookListScopeChanged = selectedFilterKey !== prevProps.selectedFilterKey ||
      selectedMediaType !== prevProps.selectedMediaType;

    if (bookListScopeChanged) {
      const nextState = {};

      if (this._selectAllRequestPending) {
        this.invalidateSelectAllRequest();
        nextState.isSelectingAll = false;
        nextState.selectAllError = translate('BookListChangedDuringSelectAll');
        nextState.selectAllErrorKind = kinds.WARNING;
      } else {
        nextState.selectAllError = null;
        nextState.selectAllErrorKind = null;
      }

      if (!this.state.isSubmittingBookSearch &&
          (this._searchBookIdsRequestPending || this.state.isConfirmSearchModalOpen)) {
        this._retrySearchBookIds = null;
        this._retrySearchScope = null;
        this.invalidateSearchBookIdsRequest();
        nextState.isConfirmSearchModalOpen = false;
        nextState.isResolvingSearchBookIds = false;
        nextState.searchBookIdsError = null;
        nextState.searchScopeChangeNotice = translate('BookListChangedDuringMassSearch');
      }

      this.setState(nextState);
    }

    // Jump bar in server-paged views is driven by server-provided buckets.
    // Avoid re-building it on every page load (itemsChanged).
    if (sortChanged ||
        viewChanged ||
        (useClientSideBooks ? itemsChanged : bucketsChanged)) {
      this.setJumpBarItems();
    }

    // Selection state must reflect the loaded items, especially during infinite scroll.
    if (selectionScopeChanged) {
      this.setSelectedState(true);
    } else if (sortChanged || itemsChanged) {
      this.setSelectedState();
    }

    if (this.state.jumpToCharacter != null) {
      this.setState({ jumpToCharacter: null });
    }
  }

  componentWillUnmount() {
    this._isUnmounted = true;
    this.invalidateSelectAllRequest();
    this.invalidateSearchBookIdsRequest();
  }

  //
  // Control

  setScrollerRef = (ref) => {
    this.setState({ scroller: ref });
  };

  getSelectedIds = () => {
    if (this.state.allUnselected) {
      return [];
    }
    return getSelectedIds(this.state.selectedState);
  };

  setSelectedState(reset = false) {
    const {
      items,
      useClientSideBooks
    } = this.props;

    const {
      selectedState,
      allSelected
    } = this.state;

    // Paged views only list the pages loaded so far, so keep selections from earlier loads.
    const newSelectedState = !useClientSideBooks && !reset ? { ...selectedState } : {};

    items.forEach((book) => {
      const isItemSelected = reset ? undefined : selectedState[book.id];

      if (isItemSelected == null) {
        newSelectedState[book.id] = !reset && allSelected;
      } else {
        newSelectedState[book.id] = isItemSelected;
      }
    });

    const selectedCount = getSelectedIds(newSelectedState).length;
    const newStateCount = Object.keys(newSelectedState).length;
    let isAllSelected = false;
    let isAllUnselected = false;

    if (selectedCount === 0) {
      isAllUnselected = true;
    } else if (selectedCount === newStateCount) {
      isAllSelected = true;
    }

    this.setState({ selectedState: newSelectedState, allSelected: isAllSelected, allUnselected: isAllUnselected });
  }

  setJumpBarItems() {
    const {
      items,
      sortKey,
      sortDirection,
      isPopulated,
      bookBuckets,
      useClientSideBooks
    } = this.props;

    const isSortableForJumpBar = sortKey === 'title' || sortKey === 'authorTitle' || sortKey === 'cleanTitle';

    // Reset if not sorting by sortName
    if (!isSortableForJumpBar) {
      this.setState({ jumpBarItems: { order: [] } });
      return;
    }

    // Server-paged views: use bucket counts from the server so the jump bar covers the full dataset.
    if (!useClientSideBooks) {
      if (!bookBuckets || bookBuckets.status !== 'succeeded' || !bookBuckets.order?.length) {
        this.setState({ jumpBarItems: { order: [] } });
        return;
      }

      const order = sortDirection === sortDirections.DESCENDING ?
        [...bookBuckets.order].reverse() :
        bookBuckets.order;

      this.setState({
        jumpBarItems: {
          characters: bookBuckets.counts,
          order
        }
      });
      return;
    }

    if (!isPopulated) {
      this.setState({ jumpBarItems: { order: [] } });
      return;
    }

    const characters = reduce(items, (acc, item) => {
      const value = typeof item[sortKey] === 'string' ? item[sortKey] : '';
      if (!value) {
        return acc;
      }
      let char = value.charAt(0).toUpperCase();

      if (!isNaN(char)) {
        char = '#';
      }

      if (char in acc) {
        acc[char] = acc[char] + 1;
      } else {
        acc[char] = 1;
      }

      return acc;
    }, {});

    const order = Object.keys(characters).sort();

    // Reverse if sorting descending
    if (sortDirection === sortDirections.DESCENDING) {
      order.reverse();
    }

    const jumpBarItems = {
      characters,
      order
    };

    this.setState({ jumpBarItems });
  }

  //
  // Listeners

  onPosterOptionsPress = () => {
    this.setState({ isPosterOptionsModalOpen: true });
  };

  onPosterOptionsModalClose = () => {
    this.setState({ isPosterOptionsModalOpen: false });
  };

  onOverviewOptionsPress = () => {
    this.setState({ isOverviewOptionsModalOpen: true });
  };

  onOverviewOptionsModalClose = () => {
    this.setState({ isOverviewOptionsModalOpen: false });
  };

  onEditorTogglePress = () => {
    if (this.state.isEditorActive) {
      this.invalidateSelectAllRequest();
      this.setState({
        isEditorActive: false,
        isSelectingAll: false,
        selectAllError: null,
        selectAllErrorKind: null
      });
    } else {
      const newState = selectAll(this.state.selectedState, false);
      newState.isEditorActive = true;
      this.setState(newState);
    }
  };

  onJumpBarItemPress = (jumpToCharacter) => {
    this.setState({ jumpToCharacter });
  };

  onSelectAllChange = ({ value }) => {
    if (!value) {
      this.invalidateSelectAllRequest();
      this.setState({
        allSelected: false,
        allUnselected: true,
        isSelectingAll: false,
        selectAllError: null,
        selectAllErrorKind: null,
        lastToggled: null,
        selectedState: {}
      });
      return;
    }

    const {
      bookQueryKey,
      bookQueryParams,
      onFetchBookIds,
      useClientSideBooks
    } = this.props;

    if (!useClientSideBooks) {
      if (this._selectAllRequestPending) {
        return;
      }

      const requestId = this.startSelectAllRequest();
      this.setState({ isSelectingAll: true, selectAllError: null, selectAllErrorKind: null });

      Promise.resolve()
        .then(() => onFetchBookIds(bookQueryKey, bookQueryParams))
        .then((response) => {
          if (requestId !== this._selectAllRequestId) {
            return;
          }

          this._selectAllRequestPending = false;
          const ids = getBookIds(response);

          if (!ids) {
            this.setState({
              isSelectingAll: false,
              selectAllError: translate('UnableToSelectAllBooks'),
              selectAllErrorKind: kinds.DANGER
            });
            return;
          }

          const nextSelectedState = {};

          ids.forEach((id) => {
            nextSelectedState[id] = true;
          });

          this.setState({
            allSelected: ids.length > 0,
            allUnselected: ids.length === 0,
            isSelectingAll: false,
            selectAllError: ids.length ? null : translate('NoBooksMatchedForSelection'),
            selectAllErrorKind: ids.length ? null : kinds.WARNING,
            lastToggled: null,
            selectedState: nextSelectedState
          });
        })
        .catch((error) => {
          if (requestId !== this._selectAllRequestId) {
            return;
          }

          this._selectAllRequestPending = false;
          this.setState({
            isSelectingAll: false,
            selectAllError: getErrorMessage(error, translate('UnableToSelectAllBooks')),
            selectAllErrorKind: kinds.DANGER
          });
        });

      return;
    }

    const nextSelectedState = {};

    this.props.items.forEach((book) => {
      nextSelectedState[book.id] = true;
    });

    this.setState({
      allSelected: true,
      allUnselected: false,
      lastToggled: null,
      selectedState: nextSelectedState
    });
  };

  onSelectAllPress = () => {
    this.onSelectAllChange({ value: !this.state.allSelected });
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    this.setState((state) => {
      return toggleSelected(state, this.props.items, id, value, shiftKey);
    });
  };

  onSaveSelected = (changes) => {
    const selectedIds = new Set(this.getSelectedIds());
    const selectedMediaTypes = new Set(
      (this.props.items || [])
        .filter((book) => selectedIds.has(book.id))
        .map((book) => book.mediaType)
        .filter((value) => value === 'audiobook' || value === 'ebook')
    );
    const payload = {
      bookIds: Array.from(selectedIds),
      ...changes
    };

    if (this.props.selectedMediaType !== 'both') {
      payload.mediaType = this.props.selectedMediaType;
    } else if (selectedMediaTypes.size === 1) {
      payload.mediaType = Array.from(selectedMediaTypes)[0];
    }

    this.props.onSaveSelected(payload);
  };

  onSearchPress = () => {
    this._retrySearchBookIds = null;
    this._retrySearchScope = null;
    this.setState({
      isConfirmSearchModalOpen: true,
      searchBookIdsError: null,
      searchBookIdsErrorKind: null,
      searchBookIdsErrorHelp: null,
      searchScopeChangeNotice: null
    });
  };

  onRefreshBookPress = (bookIds = null) => {
    if (this.state.isStartingRefresh || this.props.isRefreshingBook) {
      return;
    }

    const selectedIds = this.getSelectedIds();
    const refreshIds = bookIds || (this.state.isEditorActive && selectedIds.length > 0 ? selectedIds : []);

    this.setState({
      isStartingRefresh: true,
      refreshError: null,
      failedRefreshBookIds: refreshIds
    });

    Promise.resolve()
      .then(() => this.props.onRefreshBookPress(refreshIds))
      .then(() => {
        if (this._isUnmounted) {
          return;
        }

        if (!this.props.isRefreshingBook) {
          this.setState({ isStartingRefresh: false });
        }
      })
      .catch((error) => {
        if (this._isUnmounted) {
          return;
        }

        this.setState({
          isStartingRefresh: false,
          refreshError: getErrorMessage(error, translate('UnableToStartLibraryUpdate'))
        });
      });
  };

  onRetryRefreshPress = () => {
    this.onRefreshBookPress(this.state.failedRefreshBookIds);
  };

  onRssSyncPress = () => {
    if (this.state.isStartingRssSync || this.props.isRssSyncExecuting) {
      return;
    }

    this.setState({ isStartingRssSync: true, rssSyncError: null });
    Promise.resolve()
      .then(() => this.props.onRssSyncPress())
      .then(() => {
        if (this._isUnmounted) {
          return;
        }

        if (!this.props.isRssSyncExecuting) {
          this.setState({ isStartingRssSync: false });
        }
      })
      .catch((error) => {
        if (this._isUnmounted) {
          return;
        }

        this.setState({
          isStartingRssSync: false,
          rssSyncError: getErrorMessage(error, translate('UnableToStartRssSync'))
        });
      });
  };

  onSearchConfirmed = () => {
    const {
      items,
      useClientSideBooks,
      bookQueryKey,
      bookQueryParams,
      onFetchBookIds
    } = this.props;

    if (this._searchBookIdsRequestPending) {
      return;
    }

    if (this._retrySearchBookIds && this._retrySearchScope === this.getBookListScope()) {
      const requestId = this.startSearchBookIdsRequest();
      this.startBookSearch(this._retrySearchBookIds, requestId);
      return;
    }

    const selectedBookIds = this.getSelectedIds();

    if (this.state.isEditorActive && selectedBookIds.length > 0) {
      const requestId = this.startSearchBookIdsRequest();
      this.startBookSearch(selectedBookIds, requestId);
      return;
    }

    // Paged views only hold the loaded pages, so ask the server for every book in the list.
    if (!useClientSideBooks) {
      const requestId = this.startSearchBookIdsRequest();
      this.setState({
        isResolvingSearchBookIds: true,
        searchBookIdsError: null,
        searchBookIdsErrorKind: null,
        searchBookIdsErrorHelp: null,
        searchScopeChangeNotice: null
      });

      Promise.resolve()
        .then(() => onFetchBookIds(bookQueryKey, bookQueryParams))
        .then((response) => {
          if (requestId !== this._searchBookIdsRequestId) {
            return;
          }

          const ids = getBookIds(response);

          if (!ids || !ids.length) {
            this._searchBookIdsRequestPending = false;
            this.setState({
              isResolvingSearchBookIds: false,
              searchBookIdsError: translate(ids ? 'NoBooksMatchedForMassSearch' : 'UnableToLoadBooksForMassSearch'),
              searchBookIdsErrorKind: ids ? kinds.WARNING : kinds.DANGER,
              searchBookIdsErrorHelp: translate(ids ? 'CheckFilterBeforeRetryingMassSearch' : 'RetryToResolveBookSearch')
            });
            return;
          }

          this.startBookSearch(ids, requestId);
        })
        .catch((error) => {
          if (requestId !== this._searchBookIdsRequestId) {
            return;
          }

          this._searchBookIdsRequestPending = false;
          this.setState({
            isResolvingSearchBookIds: false,
            searchBookIdsError: getErrorMessage(error, translate('UnableToLoadBooksForMassSearch')),
            searchBookIdsErrorKind: kinds.DANGER,
            searchBookIdsErrorHelp: translate('RetryToResolveBookSearch')
          });
        });

      return;
    }

    const requestId = this.startSearchBookIdsRequest();
    this.startBookSearch(items.map((m) => m.id), requestId);
  };

  onConfirmSearchModalClose = () => {
    if (this.state.isSubmittingBookSearch) {
      return;
    }

    this._retrySearchBookIds = null;
    this._retrySearchScope = null;
    this.invalidateSearchBookIdsRequest();
    this.setState({
      isConfirmSearchModalOpen: false,
      isResolvingSearchBookIds: false,
      isSubmittingBookSearch: false,
      searchBookIdsError: null,
      searchBookIdsErrorKind: null,
      searchBookIdsErrorHelp: null
    });
  };

  getBookListScope = () => `${this.props.selectedFilterKey}:${this.props.selectedMediaType}`;

  startBookSearch = (bookIds, requestId) => {
    this._searchBookIdsRequestPending = true;
    this._retrySearchBookIds = bookIds;
    this._retrySearchScope = this.getBookListScope();
    this.setState({
      isResolvingSearchBookIds: false,
      isSubmittingBookSearch: true,
      searchBookIdsError: null,
      searchBookIdsErrorKind: null,
      searchBookIdsErrorHelp: null
    });

    Promise.resolve()
      .then(() => this.props.onSearchPress(bookIds))
      .then(() => {
        if (requestId !== this._searchBookIdsRequestId) {
          return;
        }

        this._searchBookIdsRequestPending = false;
        this._retrySearchBookIds = null;
        this._retrySearchScope = null;
        this.setState({
          isConfirmSearchModalOpen: false,
          isSubmittingBookSearch: false,
          searchBookIdsError: null,
          searchBookIdsErrorKind: null,
          searchBookIdsErrorHelp: null,
          searchScopeChangeNotice: null
        });
      })
      .catch((error) => {
        if (requestId !== this._searchBookIdsRequestId) {
          return;
        }

        this._searchBookIdsRequestPending = false;
        this.setState({
          isResolvingSearchBookIds: false,
          isSubmittingBookSearch: false,
          searchBookIdsError: getErrorMessage(error, translate('UnableToStartBookSearch')),
          searchBookIdsErrorKind: kinds.DANGER,
          searchBookIdsErrorHelp: translate('RetryToStartBookSearch')
        });
      });
  };

  startSelectAllRequest = () => {
    this._selectAllRequestPending = true;
    this._selectAllRequestId = (this._selectAllRequestId || 0) + 1;

    return this._selectAllRequestId;
  };

  invalidateSelectAllRequest = () => {
    this._selectAllRequestPending = false;
    this._selectAllRequestId = (this._selectAllRequestId || 0) + 1;
  };

  startSearchBookIdsRequest = () => {
    this._searchBookIdsRequestPending = true;
    this._searchBookIdsRequestId = (this._searchBookIdsRequestId || 0) + 1;

    return this._searchBookIdsRequestId;
  };

  invalidateSearchBookIdsRequest = () => {
    this._searchBookIdsRequestPending = false;
    this._searchBookIdsRequestId = (this._searchBookIdsRequestId || 0) + 1;
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      totalItems,
      items,
      columns,
      selectedFilterKey,
      filters,
      customFilters,
      sortKey,
      sortDirection,
      view,
      isRefreshingBook,
      isRssSyncExecuting,
      isSearching,
      isSaving,
      saveError,
      isDeleting,
      deleteError,
      onScroll,
      onSortSelect,
      onFilterSelect,
      onViewSelect,
      selectedMediaType,
      onMediaTypeChange,
      useClientSideBooks,
      bookBuckets,
      bookTotalCount,
      ...otherProps
    } = this.props;

    const {
      scroller,
      jumpBarItems,
      jumpToCharacter,
      isPosterOptionsModalOpen,
      isOverviewOptionsModalOpen,
      isConfirmSearchModalOpen,
      isEditorActive,
      isStartingRefresh,
      refreshError,
      isStartingRssSync,
      rssSyncError,
      isSelectingAll,
      selectAllError,
      selectAllErrorKind,
      isResolvingSearchBookIds,
      isSubmittingBookSearch,
      searchBookIdsError,
      searchBookIdsErrorKind,
      searchBookIdsErrorHelp,
      searchScopeChangeNotice,
      selectedState,
      allSelected,
      allUnselected
    } = this.state;

    const selectedBookIds = this.getSelectedIds();

    const ViewComponent = getViewComponent(view, useClientSideBooks);
    const isPagedView = !useClientSideBooks;
    const isPagedViewEmpty = isPagedView &&
      bookTotalCount === 0 &&
      bookBuckets?.status !== 'failed';
    const isLoaded = !!(!error && (isPagedView || (isPopulated && items.length)) && scroller);
    const hasNoAuthor = useClientSideBooks && !totalItems;
    const showNoBooks = !error && (
      isPagedViewEmpty ||
      (isPopulated && !items.length && useClientSideBooks)
    );
    const isFiltered = selectedFilterKey !== 'all';
    const showFilteredEmptyState = isFiltered && (totalItems > 0 || isPagedViewEmpty);
    const noBooksTotalItems = isPagedViewEmpty ? 0 : totalItems;

    const refreshLabel = isEditorActive && selectedBookIds.length > 0 ? translate('UpdateSelected') : translate('UpdateAll');
    const isRefreshPending = isRefreshingBook || isStartingRefresh;
    const isRssSyncPending = isRssSyncExecuting || isStartingRssSync;
    const searchIndexLabel = selectedFilterKey === 'all' ? translate('SearchAll') : translate('SearchFiltered');
    const searchEditorLabel = selectedBookIds.length > 0 ? translate('SearchSelected') : translate('SearchAll');
    const listedBookCount = isPagedView ? (bookTotalCount || 0) : items.length;
    const searchWarningCount = isEditorActive && selectedBookIds.length > 0 ? selectedBookIds.length : listedBookCount;

    return (
      <PageContent>
        <PageToolbar className={styles.toolbar}>
          <div className={styles.mediaTypeToggle}>
            <MediaTypeToggle
              selectedMediaType={selectedMediaType}
              includeBoth={true}
              onMediaTypeChange={onMediaTypeChange}
            />
          </div>

          <div className={styles.actionToolbar}>
            <PageToolbarSection>
              <PageToolbarButton
                label={refreshLabel}
                iconName={icons.REFRESH}
                spinningName={icons.REFRESH}
                isSpinning={isRefreshPending}
                isDisabled={hasNoAuthor || isRefreshPending}
                onPress={this.onRefreshBookPress}
              />

              <PageToolbarButton
                label={translate('RSSSync')}
                iconName={icons.RSS}
                isSpinning={isRssSyncPending}
                isDisabled={hasNoAuthor || isRssSyncPending}
                onPress={this.onRssSyncPress}
              />

              <PageToolbarSeparator />

              <PageToolbarButton
                label={isEditorActive ? searchEditorLabel : searchIndexLabel}
                iconName={icons.SEARCH}
                isDisabled={isSearching || !listedBookCount}
                onPress={this.onSearchPress}
              />

              <PageToolbarSeparator />

              {
                isEditorActive ?
                  <PageToolbarButton
                    label={translate('BookIndex')}
                    iconName={icons.AUTHOR_CONTINUING}
                    isDisabled={hasNoAuthor}
                    onPress={this.onEditorTogglePress}
                  /> :
                  <PageToolbarButton
                    label={translate('BookEditor')}
                    iconName={icons.EDIT}
                    isDisabled={hasNoAuthor}
                    onPress={this.onEditorTogglePress}
                  />
              }

              {
                isEditorActive ?
                  <PageToolbarButton
                    label={allSelected ? translate('UnselectAll') : translate('SelectAll')}
                    iconName={icons.CHECK_SQUARE}
                    isSpinning={isSelectingAll}
                    isDisabled={hasNoAuthor || isSelectingAll}
                    onPress={this.onSelectAllPress}
                  /> :
                  null
              }

            </PageToolbarSection>
          </div>

          <div className={styles.viewToolbar}>
            <PageToolbarSection
              alignContent={align.RIGHT}
              collapseButtons={false}
            >
              {
                view === 'table' ?
                  <TableOptionsModalWrapper
                    {...otherProps}
                    columns={columns}
                    optionsComponent={BookIndexTableOptionsConnector}
                  >
                    <PageToolbarButton
                      label={translate('Options')}
                      iconName={icons.TABLE}
                    />
                  </TableOptionsModalWrapper> :
                  null
              }

              {
                view === 'posters' ?
                  <PageToolbarButton
                    label={translate('Options')}
                    iconName={icons.POSTER}
                    isDisabled={hasNoAuthor}
                    onPress={this.onPosterOptionsPress}
                  /> :
                  null
              }

              {
                view === 'overview' ?
                  <PageToolbarButton
                    label={translate('Options')}
                    iconName={icons.OVERVIEW}
                    isDisabled={hasNoAuthor}
                    onPress={this.onOverviewOptionsPress}
                  /> :
                  null
              }

              <PageToolbarSeparator />

              <BookIndexViewMenu
                view={view}
                isDisabled={hasNoAuthor}
                onViewSelect={onViewSelect}
              />

              <BookIndexSortMenu
                sortKey={sortKey}
                sortDirection={sortDirection}
                isDisabled={hasNoAuthor}
                onSortSelect={onSortSelect}
              />

              <BookIndexFilterMenu
                selectedFilterKey={selectedFilterKey}
                filters={filters}
                customFilters={customFilters}
                isDisabled={hasNoAuthor}
                onFilterSelect={onFilterSelect}
              />
            </PageToolbarSection>
          </div>
        </PageToolbar>

        <div className={styles.pageContentBodyWrapper}>
          <PageContentBody
            registerScroller={this.setScrollerRef}
            className={styles.contentBody}
            innerClassName={styles[`${view}InnerContentBody`]}
            onScroll={onScroll}
          >
            {
              isFetching && !isPopulated &&
                <LoadingIndicator />
            }

            {
              !isFetching && !!error &&
                <div className={styles.errorMessage}>
                  {getErrorMessage(error, translate('ErrorPageBooksUnavailable'))}
                </div>
            }

            {
              !!refreshError &&
                <Alert kind={kinds.DANGER} role="alert">
                  <div>{refreshError}</div>
                  <Button onPress={this.onRetryRefreshPress}>
                    {translate('Retry')}
                  </Button>
                </Alert>
            }

            {
              !!rssSyncError &&
                <Alert kind={kinds.DANGER} role="alert">
                  <div>{rssSyncError}</div>
                  <Button onPress={this.onRssSyncPress}>
                    {translate('Retry')}
                  </Button>
                </Alert>
            }

            {
              !!selectAllError &&
                <div className={styles.operationError}>
                  <Alert
                    kind={selectAllErrorKind || kinds.DANGER}
                    role={selectAllErrorKind === kinds.WARNING ? 'status' : 'alert'}
                  >
                    {selectAllError}
                  </Alert>
                  <Button onPress={this.onSelectAllPress}>
                    {translate('Retry')}
                  </Button>
                </div>
            }

            {
              !!searchScopeChangeNotice &&
                <div className={styles.operationError}>
                  <Alert kind={kinds.INFO} role="status">
                    {searchScopeChangeNotice}
                  </Alert>
                  <Button onPress={this.onSearchPress}>
                    {translate('ReviewSearch')}
                  </Button>
                </div>
            }

            {
              isLoaded &&
                <div className={styles.contentBodyContainer}>
                  <ViewComponent
                    scroller={scroller}
                    items={items}
                    filters={filters}
                    sortKey={sortKey}
                    sortDirection={sortDirection}
                    jumpToCharacter={jumpToCharacter}
                    isEditorActive={isEditorActive}
                    allSelected={allSelected}
                    allUnselected={allUnselected}
                    onSelectedChange={this.onSelectedChange}
                    onSelectAllChange={this.onSelectAllChange}
                    selectedState={selectedState}
                    {...otherProps}
                  />
                </div>
            }

            {
              showNoBooks &&
                <NoAuthor
                  totalItems={noBooksTotalItems}
                  isFiltered={showFilteredEmptyState}
                  itemType={'books'}
                />
            }
          </PageContentBody>

          {
            isLoaded && !!jumpBarItems.order.length &&
              <PageJumpBar
                items={jumpBarItems}
                onItemPress={this.onJumpBarItemPress}
              />
          }
        </div>

        {
          isLoaded &&
            <BookIndexFooterConnector isSticky={!isEditorActive && !this.props.isSmallScreen} />
        }

        {
          isLoaded && isEditorActive &&
            <BookEditorFooter
              bookIds={selectedBookIds}
              selectedCount={selectedBookIds.length}
              isSaving={isSaving}
              saveError={saveError}
              isDeleting={isDeleting}
              deleteError={deleteError}
              onSaveSelected={this.onSaveSelected}
            />
        }

        <BookIndexPosterOptionsModal
          isOpen={isPosterOptionsModalOpen}
          onModalClose={this.onPosterOptionsModalClose}
        />

        <BookIndexOverviewOptionsModal
          isOpen={isOverviewOptionsModalOpen}
          onModalClose={this.onOverviewOptionsModalClose}

        />

        <ConfirmModal
          isOpen={isConfirmSearchModalOpen}
          kind={kinds.DANGER}
          title={translate('MassBookSearch')}
          message={
            <div>
              <div>
                {translate('MassBookSearchWarning', [searchWarningCount])}
              </div>
              <div>
                {translate('ThisCannotBeCancelled')}
              </div>
              {
                !!searchBookIdsError &&
                  <Alert
                    kind={searchBookIdsErrorKind || kinds.DANGER}
                    role={searchBookIdsErrorKind === kinds.WARNING ? 'status' : 'alert'}
                  >
                    <div>{searchBookIdsError}</div>
                    <div>{searchBookIdsErrorHelp}</div>
                  </Alert>
              }
            </div>
          }
          confirmLabel={translate(searchBookIdsError ? 'Retry' : 'Search')}
          isSpinning={isResolvingSearchBookIds || isSubmittingBookSearch}
          hideCancelButton={isSubmittingBookSearch}
          onConfirm={this.onSearchConfirmed}
          onCancel={this.onConfirmSearchModalClose}
        />
      </PageContent>
    );
  }
}

BookIndex.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  totalItems: PropTypes.number.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectedFilterKey: PropTypes.oneOfType([PropTypes.string, PropTypes.number]).isRequired,
  filters: PropTypes.arrayOf(PropTypes.object).isRequired,
  customFilters: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  sortDirection: PropTypes.oneOf(sortDirections.all),
  view: PropTypes.string.isRequired,
  isRefreshingBook: PropTypes.bool.isRequired,
  isSearching: PropTypes.bool.isRequired,
  isRssSyncExecuting: PropTypes.bool.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  onSortSelect: PropTypes.func.isRequired,
  onFilterSelect: PropTypes.func.isRequired,
  onViewSelect: PropTypes.func.isRequired,
  onRefreshBookPress: PropTypes.func.isRequired,
  onRssSyncPress: PropTypes.func.isRequired,
  onSearchPress: PropTypes.func.isRequired,
  onScroll: PropTypes.func.isRequired,
  onSaveSelected: PropTypes.func.isRequired,
  onFetchBookIds: PropTypes.func.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  bookQueryKey: PropTypes.string.isRequired,
  bookQueryParams: PropTypes.object.isRequired,
  bookBuckets: PropTypes.shape({
    counts: PropTypes.object,
    order: PropTypes.arrayOf(PropTypes.string),
    status: PropTypes.string
  }),
  bookTotalCount: PropTypes.number,
  useClientSideBooks: PropTypes.bool.isRequired,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default BookIndex;
