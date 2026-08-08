import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import VirtualTable from 'Components/Table/VirtualTable';
import VirtualTableRow from 'Components/Table/VirtualTableRow';
import { align, icons, kinds, sizes, sortDirections } from 'Helpers/Props';
import { getMediaTypeFromExtension } from 'Utilities/MediaFile/getMediaTypeFromExtension';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import translate from 'Utilities/String/translate';
import UnmappedFilesMediaTypeToggle from './UnmappedFilesMediaTypeToggle';
import UnmappedFilesTableHeader from './UnmappedFilesTableHeader';
import UnmappedFilesTableRow from './UnmappedFilesTableRow';

class UnmappedFilesTable extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.bookUnitCounts = new WeakMap();
    this.filteredItemsCache = {
      items: null,
      selectedMediaType: null,
      filteredItems: null
    };
    this.bookUnitIndexCache = {
      items: null,
      byBookUnit: null,
      byFileId: null
    };

    this.state = {
      scroller: null,
      selectionMode: 'none',
      selectedIds: new Set(),
      selectedMediaType: 'all',
      isConfirmDeleteModalOpen: false,
      isSendLogsPreviewModalOpen: false,
      isFetchingSendLogsPreview: false,
      sendLogsPreview: null,
      sendLogsPreviewError: null,
      sendLogsRequest: null,
      expandedSendLogsPreviewIndex: 0
    };
  }

  componentDidUpdate(prevProps) {
    const {
      items,
      sortKey,
      sortDirection,
      isDeleting,
      deleteError
    } = this.props;

    if (sortKey !== prevProps.sortKey ||
      sortDirection !== prevProps.sortDirection ||
      hasDifferentItemsOrOrder(prevProps.items, items)
    ) {
      this.reconcileSelectionState();
    }

    const hasFinishedDeleting = prevProps.isDeleting &&
                                !isDeleting &&
                                !deleteError;

    if (hasFinishedDeleting) {
      this.onSelectAllChange({ value: false });
    }
  }

  //
  // Control

  setScrollerRef = (ref) => {
    this.setState({ scroller: ref });
  };

  onMediaTypeChange = (mediaType) => {
    this.setState({
      selectedMediaType: mediaType,
      selectionMode: 'none',
      selectedIds: new Set()
    });
  };

  getBookUnitKey = (item) => {
    return item.importUnitKey || `file:${item.id}`;
  };

  // Grouping comes from the same backend evidence contract used by matching.
  getFilesInBookUnit = (targetItem) => {
    const targetBookUnit = this.getBookUnitKey(targetItem);
    const bookUnitIndex = this.getBookUnitIndex();

    return bookUnitIndex.byBookUnit.get(targetBookUnit) || [];
  };

  getBookUnitIndex = () => {
    const { items } = this.props;

    if (this.bookUnitIndexCache.items === items) {
      return this.bookUnitIndexCache;
    }

    const byBookUnit = new Map();
    const byFileId = new Map();

    items.forEach((item) => {
      const bookUnit = this.getBookUnitKey(item);

      if (!byBookUnit.has(bookUnit)) {
        byBookUnit.set(bookUnit, []);
      }

      byBookUnit.get(bookUnit).push(item);
      byFileId.set(item.id, item);
    });

    this.bookUnitIndexCache = {
      items,
      byBookUnit,
      byFileId
    };

    return this.bookUnitIndexCache;
  };

  getBookUnitCount = (items) => {
    if (this.bookUnitCounts.has(items)) {
      return this.bookUnitCounts.get(items);
    }

    const uniqueBookUnits = new Set(
      items.map((item) => this.getBookUnitKey(item))
    );

    const count = uniqueBookUnits.size;
    this.bookUnitCounts.set(items, count);

    return count;
  };

  formatFileCount = (count) => {
    return translate(count === 1 ? 'UnmappedFilesFileCount' : 'UnmappedFilesFilesCount', { count });
  };

  formatBookUnitCount = (count) => {
    return translate(count === 1 ? 'UnmappedFilesBookGroupCount' : 'UnmappedFilesBookGroupsCount', { count });
  };

  getFilteredItems = () => {
    const { items } = this.props;
    const { selectedMediaType } = this.state;

    if (this.filteredItemsCache.items === items &&
        this.filteredItemsCache.selectedMediaType === selectedMediaType) {
      return this.filteredItemsCache.filteredItems;
    }

    let filteredItems = items;

    // Filter by media type
    if (selectedMediaType !== 'all') {
      filteredItems = items.filter((item) => {
        const mediaType = getMediaTypeFromExtension(item.path);
        return mediaType === selectedMediaType;
      });
    }

    this.filteredItemsCache = {
      items,
      selectedMediaType,
      filteredItems
    };

    return filteredItems;
  };

  isItemSelected = (id) => {
    const { selectionMode, selectedIds } = this.state;

    if (selectionMode === 'all') {
      return !selectedIds.has(id);
    }

    if (selectionMode === 'subset') {
      return selectedIds.has(id);
    }

    return false;
  };

  isItemInSelectedMediaType = (item) => {
    const { selectedMediaType } = this.state;

    if (selectedMediaType === 'all') {
      return true;
    }

    return getMediaTypeFromExtension(item.path) === selectedMediaType;
  };

  getSelectionSummary = (filteredItems, filteredBookUnits) => {
    const { selectionMode, selectedIds } = this.state;

    if (selectionMode === 'none') {
      return {
        selectedFilesCount: 0,
        selectedBookUnitsCount: 0,
        allFilteredSelected: false
      };
    }

    if (selectionMode === 'all') {
      if (selectedIds.size === 0) {
        return {
          selectedFilesCount: filteredItems.length,
          selectedBookUnitsCount: filteredBookUnits,
          allFilteredSelected: filteredItems.length > 0
        };
      }

      let visibleExclusions = 0;
      const excludedBookUnits = new Set();
      const { byFileId } = this.getBookUnitIndex();

      selectedIds.forEach((id) => {
        const item = byFileId.get(id);

        if (item && this.isItemInSelectedMediaType(item)) {
          visibleExclusions++;
          excludedBookUnits.add(this.getBookUnitKey(item));
        }
      });

      return {
        selectedFilesCount: Math.max(0, filteredItems.length - visibleExclusions),
        selectedBookUnitsCount: Math.max(0, filteredBookUnits - excludedBookUnits.size),
        allFilteredSelected: visibleExclusions === 0 && filteredItems.length > 0
      };
    }

    let selectedFilesCount = 0;
    const selectedBookUnits = new Set();
    const { byFileId } = this.getBookUnitIndex();

    selectedIds.forEach((id) => {
      const item = byFileId.get(id);

      if (item && this.isItemInSelectedMediaType(item)) {
        selectedFilesCount++;
        selectedBookUnits.add(this.getBookUnitKey(item));
      }
    });

    return {
      selectedFilesCount,
      selectedBookUnitsCount: selectedBookUnits.size,
      allFilteredSelected: selectedFilesCount === filteredItems.length && filteredItems.length > 0
    };
  };

  getSelectedIds = () => {
    const { selectionMode, selectedIds } = this.state;

    if (selectionMode === 'none') {
      return [];
    }

    if (selectionMode === 'subset') {
      const { byFileId } = this.getBookUnitIndex();

      return Array.from(selectedIds).filter((id) => {
        const item = byFileId.get(id);
        return item && this.isItemInSelectedMediaType(item);
      });
    }

    return this.getFilteredItems()
      .filter((item) => !selectedIds.has(item.id))
      .map((item) => item.id);
  };

  buildUnmappedFilesSelection = () => {
    const { selectionMode, selectedIds } = this.state;

    if (selectionMode === 'all') {
      const exceptBookFileIds = Array.from(selectedIds);

      return exceptBookFileIds.length ?
        {
          scope: 'all',
          exceptBookFileIds
        } :
        {
          scope: 'all'
        };
    }

    if (selectionMode === 'subset') {
      const bookFileIds = this.getSelectedIds();

      if (bookFileIds.length) {
        return {
          scope: 'selected',
          bookFileIds
        };
      }
    }

    return {
      scope: 'all'
    };
  };

  reconcileSelectionState() {
    const { selectionMode, selectedIds } = this.state;

    if (selectionMode === 'none' || selectedIds.size === 0) {
      return;
    }

    const { byFileId } = this.getBookUnitIndex();
    const reconciledIds = new Set();

    selectedIds.forEach((id) => {
      if (byFileId.has(id)) {
        reconciledIds.add(id);
      }
    });

    if (reconciledIds.size !== selectedIds.size) {
      this.setState({
        selectedIds: reconciledIds,
        selectionMode: selectionMode === 'subset' && reconciledIds.size === 0 ? 'none' : selectionMode
      });
    }
  }

  onSelectAllChange = ({ value }) => {
    this.setState({
      selectionMode: value ? 'all' : 'none',
      selectedIds: new Set()
    });
  };

  onSelectAllPress = () => {
    const filteredItems = this.getFilteredItems();
    const selectionSummary = this.getSelectionSummary(filteredItems, this.getBookUnitCount(filteredItems));

    this.onSelectAllChange({ value: !selectionSummary.allFilteredSelected });
  };

  onSelectedChange = ({ id, value }) => {
    const targetItem = this.getBookUnitIndex().byFileId.get(id);

    if (!targetItem) {
      return;
    }

    // Get all files in the same book unit
    const filesInSameBookUnit = this.getFilesInBookUnit(targetItem);

    this.setState((state) => {
      const selectedIds = new Set(state.selectedIds);

      if (state.selectionMode === 'all') {
        filesInSameBookUnit.forEach((file) => {
          if (value) {
            selectedIds.delete(file.id);
          } else {
            selectedIds.add(file.id);
          }
        });

        return {
          ...state,
          selectedIds
        };
      }

      filesInSameBookUnit.forEach((file) => {
        if (value) {
          selectedIds.add(file.id);
        } else {
          selectedIds.delete(file.id);
        }
      });

      return {
        ...state,
        selectedIds,
        selectionMode: selectedIds.size > 0 ? 'subset' : 'none'
      };
    });
  };

  onDeleteUnmappedFilesPress = () => {
    if (!this.getSelectedIds().length) {
      return;
    }

    this.setState({ isConfirmDeleteModalOpen: true });
  };

  onConfirmDeleteUnmappedFiles = () => {
    const selectedIds = this.getSelectedIds();

    this.setState({ isConfirmDeleteModalOpen: false });

    if (!selectedIds.length) {
      return;
    }

    this.props.deleteUnmappedFiles(selectedIds);
  };

  onConfirmDeleteModalClose = () => {
    this.setState({ isConfirmDeleteModalOpen: false });
  };

  buildSendLogsRequest = () => {
    const { selectedMediaType } = this.state;

    return {
      minutes: 30,
      mediaType: selectedMediaType,
      unmappedFiles: this.buildUnmappedFilesSelection()
    };
  };

  onSendLogsForReview = () => {
    const request = this.buildSendLogsRequest();

    this.setState({
      isSendLogsPreviewModalOpen: true,
      isFetchingSendLogsPreview: true,
      sendLogsPreview: null,
      sendLogsPreviewError: null,
      sendLogsRequest: request
    });

    this.props.previewMatchingLogsForReview(request)
      .done((preview) => {
        this.setState({
          isFetchingSendLogsPreview: false,
          sendLogsPreview: preview,
          sendLogsPreviewError: null,
          expandedSendLogsPreviewIndex: 0
        });
      })
      .fail((xhr) => {
        this.setState({
          isFetchingSendLogsPreview: false,
          sendLogsPreview: null,
          sendLogsPreviewError: xhr?.responseJSON?.message || xhr?.responseText || 'Unable to preview matching logs'
        });
      });
  };

  onConfirmSendLogsForReview = () => {
    const { sendMatchingLogsForReview } = this.props;
    const { sendLogsRequest } = this.state;

    if (!sendLogsRequest) {
      return;
    }

    sendMatchingLogsForReview(sendLogsRequest);

    this.setState({
      isSendLogsPreviewModalOpen: false,
      sendLogsRequest: null
    });
  };

  onSendLogsPreviewModalClose = () => {
    this.setState({
      isSendLogsPreviewModalOpen: false,
      sendLogsPreviewError: null,
      sendLogsRequest: null,
      expandedSendLogsPreviewIndex: 0
    });
  };

  onSendLogsPreviewSamplePress = (index) => {
    this.setState((state) => ({
      expandedSendLogsPreviewIndex: state.expandedSendLogsPreviewIndex === index ? -1 : index
    }));
  };

  renderSendLogsPreviewModal = () => {
    const {
      isSendingLogs
    } = this.props;

    const {
      isSendLogsPreviewModalOpen,
      isFetchingSendLogsPreview,
      sendLogsPreview,
      sendLogsPreviewError,
      expandedSendLogsPreviewIndex
    } = this.state;

    const hasPreviewEntries = (sendLogsPreview?.totalEntries || 0) > 0;
    const sendDisabled = isFetchingSendLogsPreview || !!sendLogsPreviewError || !hasPreviewEntries;

    return (
      <Modal
        isOpen={isSendLogsPreviewModalOpen}
        size={sizes.LARGE}
        onModalClose={this.onSendLogsPreviewModalClose}
      >
        <ModalContent onModalClose={this.onSendLogsPreviewModalClose}>
          <ModalHeader>
            {translate('UnmappedFilesSendLogsHeader')}
          </ModalHeader>

          <ModalBody>
            <Alert kind={kinds.INFO}>
              {translate('UnmappedFilesSendLogsIntro')}
            </Alert>

            {
              isFetchingSendLogsPreview &&
                <LoadingIndicator />
            }

            {
              !!sendLogsPreviewError &&
                <Alert kind={kinds.DANGER}>
                  {sendLogsPreviewError}
                </Alert>
            }

            {
              !isFetchingSendLogsPreview && !sendLogsPreviewError && sendLogsPreview &&
                <div>
                  <div style={{ marginBottom: '12px' }}>
                    <strong>{sendLogsPreview.totalEntries}</strong> {translate('UnmappedFilesSendLogsEntriesWillBeSent')}
                    {sendLogsPreview.mediaType && sendLogsPreview.mediaType !== 'all' ? ` ${translate('UnmappedFilesSendLogsForMediaType', { mediaType: sendLogsPreview.mediaType })}` : ''}
                    {sendLogsPreview.scope ? ` ${translate('UnmappedFilesSendLogsScopeSuffix', { scope: sendLogsPreview.scope })}` : ''}.
                  </div>

                  {
                    !hasPreviewEntries &&
                      <Alert kind={kinds.INFO}>
                        {translate('UnmappedFilesSendLogsNoEntries')}
                      </Alert>
                  }

                  {
                    hasPreviewEntries &&
                      <div>
                        <div style={{ marginBottom: '8px', fontWeight: 600 }}>
                          {translate('UnmappedFilesSendLogsSampleEntriesIntro')}
                        </div>

                        <div style={{ display: 'grid', gap: '10px' }}>
                          {
                            (sendLogsPreview.samples || []).map((entry, index) => {
                              const tags = entry.tags || {};
                              const tagKeys = Object.keys(tags);
                              const primaryLine = entry.decision || entry.reason || (entry.success ? 'Matched' : 'No match');
                              const rejectionLine = entry.topRejectionReason ?
                                `${entry.topRejectionReason}${entry.topRejectionDetail ? `: ${entry.topRejectionDetail}` : ''}` :
                                null;
                              const isExpanded = expandedSendLogsPreviewIndex === index;

                              return (
                                <div
                                  key={`${entry.path || entry.fileName}-${index}`}
                                >
                                  <Link
                                    style={{
                                      display: 'block',
                                      width: '100%',
                                      padding: '10px 12px',
                                      border: isExpanded ? '1px solid rgba(255, 255, 255, 0.35)' : '1px solid rgba(255, 255, 255, 0.12)',
                                      borderRadius: '4px',
                                      background: isExpanded ? 'rgba(255, 255, 255, 0.1)' : 'rgba(255, 255, 255, 0.06)'
                                    }}
                                    onPress={() => this.onSendLogsPreviewSamplePress(index)}
                                  >
                                    <div style={{ fontWeight: 600 }}>
                                      {entry.path || entry.fileName || '(unknown file)'}
                                    </div>

                                    {
                                      entry.path && entry.fileName && entry.path !== entry.fileName &&
                                        <div style={{ marginTop: '4px' }}>
                                          {translate('UnmappedFilesSendLogsFileLabel')} {entry.fileName}
                                        </div>
                                    }

                                    <div style={{ marginTop: '4px' }}>
                                      {primaryLine}
                                    </div>

                                    {
                                      rejectionLine &&
                                        <div style={{ marginTop: '4px' }}>
                                          {translate('UnmappedFilesSendLogsTopRejectionLabel')} {rejectionLine}
                                        </div>
                                    }

                                    {
                                      entry.topRejectionTitle &&
                                        <div style={{ marginTop: '4px' }}>
                                          {translate('UnmappedFilesSendLogsCandidateLabel')} {entry.topRejectionTitle}
                                        </div>
                                    }

                                    {
                                      tagKeys.length > 0 &&
                                        <div style={{ marginTop: '4px' }}>
                                          {translate('UnmappedFilesSendLogsTagsLabel')} {tagKeys.slice(0, 5).join(', ')}
                                        </div>
                                    }
                                  </Link>

                                  {
                                    isExpanded &&
                                      <div style={{ marginTop: '8px' }}>
                                        <div style={{ marginBottom: '6px' }}>
                                          {translate('UnmappedFilesSendLogsExactJsonExplainerStart')} <code>p</code> {translate('UnmappedFilesSendLogsExactJsonPathExplainer')} <code>f</code> {translate('UnmappedFilesSendLogsExactJsonBasenameExplainer')}
                                        </div>

                                        <pre style={{
                                          maxHeight: '260px',
                                          margin: 0,
                                          padding: '10px 12px',
                                          overflow: 'auto',
                                          borderRadius: '4px',
                                          background: 'rgba(0, 0, 0, 0.25)',
                                          whiteSpace: 'pre-wrap',
                                          wordBreak: 'break-word'
                                        }}
                                        >
                                          {entry.uploadEntryJson}
                                        </pre>
                                      </div>
                                  }
                                </div>
                              );
                            })
                          }
                        </div>
                      </div>
                  }
                </div>
            }
          </ModalBody>

          <ModalFooter>
            <Button onPress={this.onSendLogsPreviewModalClose}>
              {translate('Cancel')}
            </Button>

            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={isSendingLogs}
              isDisabled={sendDisabled}
              onPress={this.onConfirmSendLogsForReview}
            >
              {translate('Send')}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  };

  rowRenderer = ({ key, rowIndex, style }) => {
    const {
      columns,
      deleteUnmappedFile,
      fetchUnmappedFiles
    } = this.props;

    const filteredItems = this.getFilteredItems();
    const item = filteredItems[rowIndex];
    const itemMediaType = getMediaTypeFromExtension(item.path);
    const bookUnitFileIds = this.getFilesInBookUnit(item)
      .filter((file) => getMediaTypeFromExtension(file.path) === itemMediaType)
      .map((file) => file.id);

    return (
      <VirtualTableRow
        key={key}
        style={style}
      >
        <UnmappedFilesTableRow
          key={item.id}
          columns={columns}
          isSelected={this.isItemSelected(item.id)}
          onSelectedChange={this.onSelectedChange}
          deleteUnmappedFile={deleteUnmappedFile}
          onImportComplete={fetchUnmappedFiles}
          bookUnitFileIds={bookUnitFileIds}
          {...item}
        />
      </VirtualTableRow>
    );
  };

  render() {

    const {
      isFetching,
      isPopulated,
      isDeleting,
      error,
      items,
      columns,
      sortKey,
      sortDirection,
      onTableOptionChange,
      onSortPress,
      isRefreshingFiles,
      isRetryMatching,
      onRefreshUnmappedFilesPress,
      onRetryUnmappedMatchPress,
      ...otherProps
    } = this.props;

    const {
      scroller,
      selectedMediaType,
      isConfirmDeleteModalOpen
    } = this.state;

    const filteredItems = this.getFilteredItems();

    // Calculate stats for all items and filtered items
    const totalFiles = items.length;
    const totalBookUnits = this.getBookUnitCount(items);
    const filteredFiles = filteredItems.length;
    const filteredBookUnits = this.getBookUnitCount(filteredItems);

    const selectionSummary = this.getSelectionSummary(filteredItems, filteredBookUnits);
    const selectedBookUnitsCount = selectionSummary.selectedBookUnitsCount;
    const selectedFilesCount = selectionSummary.selectedFilesCount;
    const allSelected = selectionSummary.allFilteredSelected;
    const allUnselected = selectedFilesCount === 0;

    // These are evidence-backed import groups, not matched Book identities yet.
    let statsDisplay = '';
    if (totalFiles > 0) {
      const baseStats = selectedMediaType === 'all' ?
        `${this.formatFileCount(totalFiles)} • ${this.formatBookUnitCount(totalBookUnits)}` :
        `${this.formatFileCount(filteredFiles)} / ${this.formatFileCount(totalFiles)} • ${this.formatBookUnitCount(filteredBookUnits)} / ${this.formatBookUnitCount(totalBookUnits)}`;

      if (selectedBookUnitsCount > 0) {
        statsDisplay = translate('UnmappedFilesSelectedStats', {
          baseStats,
          files: this.formatFileCount(selectedFilesCount),
          bookGroups: this.formatBookUnitCount(selectedBookUnitsCount)
        });
      } else {
        statsDisplay = baseStats;
      }
    }

    const unmappedCount = filteredItems.length;
    const titleWithCount = unmappedCount > 0 ?
      `${translate('UnmappedFiles')} (${unmappedCount})` :
      translate('UnmappedFiles');

    return (
      <PageContent title={titleWithCount}>
        <PageToolbar>
          <PageToolbarSection>
            <UnmappedFilesMediaTypeToggle
              selectedMediaType={selectedMediaType}
              onMediaTypeChange={this.onMediaTypeChange}
            />
            <PageToolbarButton
              label={selectedBookUnitsCount > 0 ? `${translate('UnmappedFilesRefreshFiles')} (${this.formatBookUnitCount(selectedBookUnitsCount)})` : translate('UnmappedFilesRefreshFiles')}
              iconName={icons.RESCAN}
              isDisabled={isPopulated && !error && !filteredItems.length}
              isSpinning={isRefreshingFiles}
              onPress={() => {
                onRefreshUnmappedFilesPress(selectedMediaType, this.buildUnmappedFilesSelection());
              }}
            />
            <PageToolbarButton
              label={selectedBookUnitsCount > 0 ? `${translate('UnmappedFilesRetryMatch')} (${this.formatBookUnitCount(selectedBookUnitsCount)})` : translate('UnmappedFilesRetryMatch')}
              iconName={icons.SEARCH}
              isDisabled={isPopulated && !error && !filteredItems.length}
              isSpinning={isRetryMatching}
              onPress={() => {
                onRetryUnmappedMatchPress(selectedMediaType, this.buildUnmappedFilesSelection());
              }}
            />
            <PageToolbarButton
              label={translate('DeleteSelected')}
              iconName={icons.DELETE}
              isDisabled={selectedFilesCount === 0}
              isSpinning={isDeleting}
              onPress={this.onDeleteUnmappedFilesPress}
            />
            <PageToolbarButton
              label={translate('UnmappedFilesSendLogsButton')}
              iconName={icons.BUG}
              title={translate('UnmappedFilesSendLogsHeader')}
              isDisabled={!isPopulated || !filteredItems.length}
              isSpinning={this.props.isSendingLogs}
              onPress={this.onSendLogsForReview}
            />
            {totalFiles > 0 && (
              <div style={{
                display: 'inline-flex',
                alignItems: 'center',
                marginLeft: '20px',
                padding: '8px 16px',
                backgroundColor: 'rgba(255, 255, 255, 0.1)',
                borderRadius: '20px',
                fontSize: '14px',
                fontWeight: '500',
                transition: 'all 0.3s ease'
              }}
              >
                {statsDisplay}
              </div>
            )}
          </PageToolbarSection>

          <PageToolbarSection alignContent={align.RIGHT}>
            <TableOptionsModalWrapper
              {...otherProps}
              columns={columns}
              onTableOptionChange={onTableOptionChange}
            >
              <PageToolbarButton
                label={translate('Options')}
                iconName={icons.TABLE}
              />
            </TableOptionsModalWrapper>

          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody
          registerScroller={this.setScrollerRef}
        >
          {
            isFetching && !isPopulated &&
              <LoadingIndicator />
          }

          {
            isPopulated && !error && !filteredItems.length &&
              <Alert kind={kinds.INFO}>
                {translate('UnmappedFilesAllMatched')}
              </Alert>
          }

          {
            isPopulated && !error && !!filteredItems.length && scroller &&
              <VirtualTable
                items={filteredItems}
                columns={columns}
                scroller={scroller}
                isSmallScreen={false}
                overscanRowCount={10}
                rowRenderer={this.rowRenderer}
                header={
                  <UnmappedFilesTableHeader
                    columns={columns}
                    sortKey={sortKey}
                    sortDirection={sortDirection}
                    onTableOptionChange={onTableOptionChange}
                    onSortPress={onSortPress}
                    allSelected={allSelected}
                    allUnselected={allUnselected}
                    onSelectAllChange={this.onSelectAllChange}
                  />
                }
                sortKey={sortKey}
                sortDirection={sortDirection}
              />
          }
        </PageContentBody>

        <ConfirmModal
          isOpen={isConfirmDeleteModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteSelectedBookFiles')}
          message={translate('DeleteSelectedBookFilesMessageText')}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteUnmappedFiles}
          onCancel={this.onConfirmDeleteModalClose}
        />

        {this.renderSendLogsPreviewModal()}
      </PageContent>
    );
  }
}

UnmappedFilesTable.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isDeleting: PropTypes.bool.isRequired,
  isSendingLogs: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string,
  sortDirection: PropTypes.oneOf(sortDirections.all),
  onTableOptionChange: PropTypes.func.isRequired,
  onSortPress: PropTypes.func.isRequired,
  deleteUnmappedFile: PropTypes.func.isRequired,
  deleteUnmappedFiles: PropTypes.func.isRequired,
  fetchUnmappedFiles: PropTypes.func.isRequired,
  isRefreshingFiles: PropTypes.bool.isRequired,
  isRetryMatching: PropTypes.bool.isRequired,
  onRefreshUnmappedFilesPress: PropTypes.func.isRequired,
  onRetryUnmappedMatchPress: PropTypes.func.isRequired,
  previewMatchingLogsForReview: PropTypes.func.isRequired,
  sendMatchingLogsForReview: PropTypes.func.isRequired
};

export default UnmappedFilesTable;
