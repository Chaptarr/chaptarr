import reduce from 'lodash/reduce';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import MediaTypeToggle from 'Author/Details/MediaTypeToggle';
import NoAuthor from 'Author/NoAuthor';
import BookEditorFooter from 'Book/Editor/BookEditorFooter';
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
import BookIndexOverviewOptionsModal from './Overview/Options/BookIndexOverviewOptionsModal';
import BookIndexPostersConnector from './Posters/BookIndexPostersConnector';
import BookIndexPostersInfiniteConnector from './Posters/BookIndexPostersInfiniteConnector';
import BookIndexPosterOptionsModal from './Posters/Options/BookIndexPosterOptionsModal';
import BookIndexTableConnector from './Table/BookIndexTableConnector';
import BookIndexTableOptionsConnector from './Table/BookIndexTableOptionsConnector';
import styles from './BookIndex.css';

function getViewComponent(view, useClientSidePosters) {
  if (view === 'posters') {
    return useClientSidePosters ? BookIndexPostersConnector : BookIndexPostersInfiniteConnector;
  }

  if (view === 'overview') {
    return BookIndexOverviewsConnector;
  }

  return BookIndexTableConnector;
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
      isSelectingAll: false,
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {}
    };
  }

  componentDidMount() {
    this.setJumpBarItems();
    this.setSelectedState();
  }

  componentDidUpdate(prevProps) {
    const {
      items,
      sortKey,
      sortDirection,
      view,
      selectedFilterKey,
      selectedMediaType,
      posterBuckets
    } = this.props;

    const itemsChanged = hasDifferentItemsOrOrder(prevProps.items, items);
    const bucketsChanged = prevProps.posterBuckets !== posterBuckets;

    const sortChanged = sortKey !== prevProps.sortKey || sortDirection !== prevProps.sortDirection;
    const viewChanged = view !== prevProps.view;
    const selectionScopeChanged = viewChanged ||
      selectedFilterKey !== prevProps.selectedFilterKey ||
      selectedMediaType !== prevProps.selectedMediaType;

    // Jump bar in infinite posters view is driven by server-provided buckets.
    // Avoid re-building it on every infinite scroll page load (itemsChanged).
    if (sortChanged ||
        viewChanged ||
        (view === 'posters' ? bucketsChanged : itemsChanged)) {
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
      view
    } = this.props;

    const {
      selectedState,
      allSelected
    } = this.state;

    const newSelectedState = view === 'posters' && !reset ? { ...selectedState } : {};

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
      view,
      posterBuckets,
      useClientSidePosters
    } = this.props;

    const isSortableForJumpBar = sortKey === 'title' || sortKey === 'authorTitle' || sortKey === 'cleanTitle';

    // Reset if not sorting by sortName
    if (!isSortableForJumpBar) {
      this.setState({ jumpBarItems: { order: [] } });
      return;
    }

    // Infinite scroll posters view: use bucket counts from the server so jump bar covers the full dataset.
    if (view === 'posters' && !useClientSidePosters) {
      if (!posterBuckets || posterBuckets.status !== 'succeeded' || !posterBuckets.order?.length) {
        this.setState({ jumpBarItems: { order: [] } });
        return;
      }

      const order = sortDirection === sortDirections.DESCENDING ?
        [...posterBuckets.order].reverse() :
        posterBuckets.order;

      this.setState({
        jumpBarItems: {
          characters: posterBuckets.counts,
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
      this.setState({ isEditorActive: false });
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
      this.setState({
        allSelected: false,
        allUnselected: true,
        lastToggled: null,
        selectedState: {}
      });
      return;
    }

    const {
      view,
      posterQueryKey,
      posterQueryParams,
      onFetchBookIds,
      useClientSidePosters
    } = this.props;

    if (view === 'posters' && !useClientSidePosters) {
      this.setState({ isSelectingAll: true });

      Promise.resolve(onFetchBookIds(posterQueryKey, posterQueryParams))
        .then((response) => {
          const ids = Array.isArray(response) ? response : (response?.ids || []);
          const nextSelectedState = {};

          ids.forEach((id) => {
            nextSelectedState[id] = true;
          });

          this.setState({
            allSelected: ids.length > 0,
            allUnselected: ids.length === 0,
            isSelectingAll: false,
            lastToggled: null,
            selectedState: nextSelectedState
          });
        })
        .catch(() => {
          this.setState({ isSelectingAll: false });
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
    this.props.onSaveSelected({
      bookIds: this.getSelectedIds(),
      mediaType: this.props.selectedMediaType,
      ...changes
    });
  };

  onSearchPress = () => {
    this.setState({ isConfirmSearchModalOpen: true });
  };

  onRefreshBookPress = () => {
    const selectedIds = this.getSelectedIds();
    const refreshIds = this.state.isEditorActive && selectedIds.length > 0 ? selectedIds : [];

    this.props.onRefreshBookPress(refreshIds);
  };

  onSearchConfirmed = () => {
    const selectedBookIds = this.getSelectedIds();
    const searchIds = this.state.isEditorActive && selectedBookIds.length > 0 ? selectedBookIds : this.props.items.map((m) => m.id);

    this.props.onSearchPress(searchIds);
    this.setState({ isConfirmSearchModalOpen: false });
  };

  onConfirmSearchModalClose = () => {
    this.setState({ isConfirmSearchModalOpen: false });
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
      onRssSyncPress,
      selectedMediaType,
      onMediaTypeChange,
      useClientSidePosters,
      posterBuckets,
      posterTotalCount,
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
      isSelectingAll,
      selectedState,
      allSelected,
      allUnselected
    } = this.state;

    const selectedBookIds = this.getSelectedIds();

    const ViewComponent = getViewComponent(view, useClientSidePosters);
    const isInfinitePostersView = view === 'posters' && !useClientSidePosters;
    const isInfinitePostersEmpty = isInfinitePostersView &&
      posterTotalCount === 0 &&
      posterBuckets?.status !== 'failed';
    const isLoaded = !!(!error && (isInfinitePostersView || (isPopulated && items.length)) && scroller);
    const hasNoAuthor = (view !== 'posters' || useClientSidePosters) && !totalItems;
    const showNoBooks = !error && (
      isInfinitePostersEmpty ||
      (isPopulated && !items.length && (view !== 'posters' || useClientSidePosters))
    );
    const isFiltered = selectedFilterKey !== 'all';
    const showFilteredEmptyState = isFiltered && (totalItems > 0 || isInfinitePostersEmpty);
    const noBooksTotalItems = isInfinitePostersEmpty ? 0 : totalItems;

    const refreshLabel = isEditorActive && selectedBookIds.length > 0 ? translate('UpdateSelected') : translate('UpdateAll');
    const searchIndexLabel = selectedFilterKey === 'all' ? translate('SearchAll') : translate('SearchFiltered');
    const searchEditorLabel = selectedBookIds.length > 0 ? translate('SearchSelected') : translate('SearchAll');
    const searchWarningCount = isEditorActive && selectedBookIds.length > 0 ? selectedBookIds.length : items.length;

    return (
      <PageContent>
        <PageToolbar>
          <PageToolbarSection>
            <MediaTypeToggle
              selectedMediaType={selectedMediaType}
              onMediaTypeChange={onMediaTypeChange}
            />

            <PageToolbarButton
              label={refreshLabel}
              iconName={icons.REFRESH}
              spinningName={icons.REFRESH}
              isSpinning={isRefreshingBook}
              onPress={this.onRefreshBookPress}
            />

            <PageToolbarButton
              label={translate('RSSSync')}
              iconName={icons.RSS}
              isSpinning={isRssSyncExecuting}
              isDisabled={hasNoAuthor}
              onPress={onRssSyncPress}
            />

            <PageToolbarSeparator />

            <PageToolbarButton
              label={isEditorActive ? searchEditorLabel : searchIndexLabel}
              iconName={icons.SEARCH}
              isDisabled={isSearching || !items.length}
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

          <PageToolbarSection
            alignContent={align.RIGHT}
            collapseButtons={true}
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
                  {getErrorMessage(error, 'Failed to load books from API')}
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
            <BookIndexFooterConnector isSticky={!isEditorActive} />
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
            </div>
          }
          confirmLabel={translate('Search')}
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
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  posterQueryKey: PropTypes.string.isRequired,
  posterQueryParams: PropTypes.object.isRequired,
  posterBuckets: PropTypes.shape({
    counts: PropTypes.object,
    order: PropTypes.arrayOf(PropTypes.string),
    status: PropTypes.string
  }),
  posterTotalCount: PropTypes.number,
  useClientSidePosters: PropTypes.bool.isRequired,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default BookIndex;
