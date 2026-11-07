import isEmpty from 'lodash/isEmpty';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import PageToolbarStatusButton from 'Components/Page/Toolbar/PageToolbarStatusButton';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import TablePager from 'Components/Table/TablePager';
import { align, icons, kinds } from 'Helpers/Props';
import getRemovedItems from 'Utilities/Object/getRemovedItems';
import hasDifferentItems from 'Utilities/Object/hasDifferentItems';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import removeOldSelectedState from 'Utilities/Table/removeOldSelectedState';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import QueueOptionsConnector from './QueueOptionsConnector';
import QueueRowConnector from './QueueRowConnector';
import RemoveQueueItemModal from './RemoveQueueItemModal';

class Queue extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._shouldBlockRefresh = false;

    this.state = {
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {},
      isConfirmRemoveModalOpen: false,
      isConfirmAutoAddAuthorsModalOpen: false,
      items: props.items
    };
  }

  shouldComponentUpdate() {
    if (this._shouldBlockRefresh) {
      return false;
    }

    return true;
  }

  componentDidUpdate(prevProps) {
    const {
      items,
      isFetching,
      isBooksFetching
    } = this.props;

    if (
      (!isBooksFetching && prevProps.isBooksFetching) ||
      (!isFetching && prevProps.isFetching) ||
      hasDifferentItems(prevProps.items, items)
    ) {
      this.setState((state) => {
        return {
          ...removeOldSelectedState(state, getRemovedItems(prevProps.items, items)),
          items
        };
      });

      return;
    }

    const nextState = {};

    if (prevProps.items !== items) {
      nextState.items = items;
    }

    if (!isEmpty(nextState)) {
      this.setState(nextState);
    }
  }

  //
  // Control

  getSelectedIds = () => {
    return getSelectedIds(this.state.selectedState);
  };

  //
  // Listeners

  onQueueRowModalOpenOrClose = (isOpen) => {
    this._shouldBlockRefresh = isOpen;
  };

  onSelectAllChange = ({ value }) => {
    this.setState(selectAll(this.state.selectedState, value));
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    this.setState((state) => {
      return toggleSelected(state, this.props.items, id, value, shiftKey);
    });
  };

  onGrabSelectedPress = () => {
    this.props.onGrabSelectedPress(this.getSelectedPendingIds());
  };

  onRetryImportSelectedPress = () => {
    this.props.onRetryImportSelectedPress(this.getSelectedRetryImportDownloadIds());
  };

  onAutoAddAuthorsPress = () => {
    if (this.props.autoAddMissingAuthorsFromCompletedDownloads) {
      this.props.onAutoAddAuthorsPress(false);
      return;
    }

    this.setState({ isConfirmAutoAddAuthorsModalOpen: true }, () => {
      this._shouldBlockRefresh = true;
    });
  };

  onAutoAddAuthorsConfirmed = () => {
    this._shouldBlockRefresh = false;
    this.props.onAutoAddAuthorsPress(true);
    this.setState({ isConfirmAutoAddAuthorsModalOpen: false });
  };

  onConfirmAutoAddAuthorsModalClose = () => {
    this._shouldBlockRefresh = false;
    this.setState({ isConfirmAutoAddAuthorsModalOpen: false });
  };

  onRemoveSelectedPress = () => {
    this.setState({ isConfirmRemoveModalOpen: true }, () => {
      this._shouldBlockRefresh = true;
    });
  };

  onRemoveSelectedConfirmed = (payload) => {
    this._shouldBlockRefresh = false;
    this.props.onRemoveSelectedPress({ ids: this.getSelectedIds(), ...payload });
    this.setState({ isConfirmRemoveModalOpen: false });
  };

  onConfirmRemoveModalClose = () => {
    this._shouldBlockRefresh = false;
    this.setState({ isConfirmRemoveModalOpen: false });
  };

  getSelectedRetryImportDownloadIds = () => {
    const selectedIds = this.getSelectedIds();

    return this.state.items
      .filter((item) => {
        return selectedIds.indexOf(item.id) > -1 && item.canRetryImport && item.downloadId;
      })
      .map((item) => item.downloadId);
  };

  getSelectedPendingIds = () => {
    const selectedIds = this.getSelectedIds();

    return this.state.items
      .filter((item) => {
        return selectedIds.indexOf(item.id) > -1 &&
          (item.status === 'delay' || item.status === 'downloadClientUnavailable');
      })
      .map((item) => item.id);
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      isAuthorFetching,
      isAuthorPopulated,
      isBooksFetching,
      isBooksPopulated,
      booksError,
      columns,
      totalRecords,
      isGrabbing,
      isRemoving,
      isRetryingImport,
      autoAddMissingAuthorsFromCompletedDownloads,
      isAutoAddMissingAuthorsPopulated,
      isAutoAddMissingAuthorsSaving,
      isRefreshMonitoredDownloadsExecuting,
      onRefreshPress,
      ...otherProps
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState,
      isConfirmRemoveModalOpen,
      isConfirmAutoAddAuthorsModalOpen,
      items
    } = this.state;

    const isRefreshing = isFetching || isAuthorFetching || isBooksFetching || isRefreshMonitoredDownloadsExecuting;
    // Show queue once the queue itself is populated, regardless of book/author population status
    // Items without bookIds can still be displayed
    const isAllPopulated = isPopulated;
    const hasError = error || booksError;
    const selectedIds = this.getSelectedIds();
    const selectedCount = selectedIds.length;
    const disableSelectedActions = selectedCount === 0;
    const selectedRetryImportDownloadIds = this.getSelectedRetryImportDownloadIds();
    const disableRetryImportSelected = selectedRetryImportDownloadIds.length === 0;
    const selectedPendingIds = this.getSelectedPendingIds();
    const disableGrabSelected = selectedPendingIds.length === 0;

    return (
      <PageContent title={translate('Queue')}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label={translate('Refresh')}
              iconName={icons.REFRESH}
              isSpinning={isRefreshing}
              onPress={onRefreshPress}
            />

            <PageToolbarButton
              label={translate('RetryImportSelected')}
              iconName={icons.RESTART}
              isDisabled={disableRetryImportSelected}
              isSpinning={isRetryingImport}
              onPress={this.onRetryImportSelectedPress}
            />

            <PageToolbarSeparator />

            <PageToolbarStatusButton
              label={translate('AutoAddAuthors')}
              iconName={icons.ADD_MISSING_AUTHORS}
              isEnabled={autoAddMissingAuthorsFromCompletedDownloads}
              enabledTitle={translate('AutoAddMissingAuthorsEnabledTooltip')}
              disabledTitle={translate('AutoAddMissingAuthorsDisabledTooltip')}
              isDisabled={!isAutoAddMissingAuthorsPopulated || isAutoAddMissingAuthorsSaving}
              onPress={this.onAutoAddAuthorsPress}
            />

            <PageToolbarSeparator />

            <PageToolbarButton
              label={translate('GrabSelected')}
              iconName={icons.DOWNLOAD}
              isDisabled={disableGrabSelected}
              isSpinning={isGrabbing}
              onPress={this.onGrabSelectedPress}
            />

            <PageToolbarButton
              label={translate('RemoveSelected')}
              iconName={icons.REMOVE}
              isDisabled={disableSelectedActions}
              isSpinning={isRemoving}
              onPress={this.onRemoveSelectedPress}
            />
          </PageToolbarSection>

          <PageToolbarSection
            alignContent={align.RIGHT}
          >
            <TableOptionsModalWrapper
              columns={columns}
              {...otherProps}
              optionsComponent={QueueOptionsConnector}
            >
              <PageToolbarButton
                label={translate('Options')}
                iconName={icons.TABLE}
              />
            </TableOptionsModalWrapper>
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          {
            isRefreshing && !isAllPopulated ?
              <LoadingIndicator /> :
              null
          }

          {
            !isRefreshing && hasError ?
              <Alert kind={kinds.DANGER}>
                {translate('FailedToLoadQueue')}
              </Alert> :
              null
          }

          {
            isAllPopulated && !hasError && !items.length ?
              <Alert kind={kinds.INFO}>
                {translate('QueueIsEmpty')}
              </Alert> :
              null
          }

          {
            isAllPopulated && !hasError && !!items.length ?
              <div>
                <Table
                  columns={columns}
                  selectAll={true}
                  allSelected={allSelected}
                  allUnselected={allUnselected}
                  {...otherProps}
                  optionsComponent={QueueOptionsConnector}
                  onSelectAllChange={this.onSelectAllChange}
                >
                  <TableBody>
                    {
                      items.map((item) => {
                        return (
                          <QueueRowConnector
                            key={item.id}
                            bookId={item.bookId}
                            isSelected={selectedState[item.id]}
                            columns={columns}
                            {...item}
                            onSelectedChange={this.onSelectedChange}
                            onQueueRowModalOpenOrClose={this.onQueueRowModalOpenOrClose}
                          />
                        );
                      })
                    }
                  </TableBody>
                </Table>

                <TablePager
                  totalRecords={totalRecords}
                  isFetching={isRefreshing}
                  {...otherProps}
                />
              </div> :
              null
          }
        </PageContentBody>

        <ConfirmModal
          isOpen={isConfirmAutoAddAuthorsModalOpen}
          kind={kinds.WARNING}
          title={translate('AutoAddMissingAuthorsConfirmTitle')}
          message={translate('AutoAddMissingAuthorsConfirmMessage')}
          confirmLabel={translate('Enable')}
          onConfirm={this.onAutoAddAuthorsConfirmed}
          onCancel={this.onConfirmAutoAddAuthorsModalClose}
        />

        <RemoveQueueItemModal
          isOpen={isConfirmRemoveModalOpen}
          selectedCount={selectedCount}
          canChangeCategory={isConfirmRemoveModalOpen && (
            selectedIds.every((id) => {
              const item = items.find((i) => i.id === id);

              return !!(item && item.downloadClientHasPostImportCategory);
            })
          )}
          canIgnore={isConfirmRemoveModalOpen && (
            selectedIds.every((id) => {
              const item = items.find((i) => i.id === id);

              return !!(item && item.downloadId);
            })
          )}
          isPending={isConfirmRemoveModalOpen && (
            selectedIds.every((id) => {
              const item = items.find((i) => i.id === id);

              if (!item) {
                return false;
              }

              return item.status === 'delay' || item.status === 'downloadClientUnavailable';
            })
          )}
          onRemovePress={this.onRemoveSelectedConfirmed}
          onModalClose={this.onConfirmRemoveModalClose}
        />
      </PageContent>
    );
  }
}

Queue.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isAuthorFetching: PropTypes.bool.isRequired,
  isAuthorPopulated: PropTypes.bool.isRequired,
  isBooksFetching: PropTypes.bool.isRequired,
  isBooksPopulated: PropTypes.bool.isRequired,
  booksError: PropTypes.object,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  totalRecords: PropTypes.number,
  isGrabbing: PropTypes.bool.isRequired,
  isRemoving: PropTypes.bool.isRequired,
  isRetryingImport: PropTypes.bool.isRequired,
  autoAddMissingAuthorsFromCompletedDownloads: PropTypes.bool.isRequired,
  isAutoAddMissingAuthorsPopulated: PropTypes.bool.isRequired,
  isAutoAddMissingAuthorsSaving: PropTypes.bool.isRequired,
  isRefreshMonitoredDownloadsExecuting: PropTypes.bool.isRequired,
  onRefreshPress: PropTypes.func.isRequired,
  onRetryImportSelectedPress: PropTypes.func.isRequired,
  onAutoAddAuthorsPress: PropTypes.func.isRequired,
  onGrabSelectedPress: PropTypes.func.isRequired,
  onRemoveSelectedPress: PropTypes.func.isRequired
};

Queue.defaultProps = {
  count: 0
};

export default Queue;
