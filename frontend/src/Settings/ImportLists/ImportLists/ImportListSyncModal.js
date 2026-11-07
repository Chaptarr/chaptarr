import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';
import styles from './ImportListSyncModal.css';

function getErrorMessage(xhr) {
  const responseJson = xhr?.responseJSON;
  if (Array.isArray(responseJson)) {
    const messages = responseJson
      .map((error) => error?.errorMessage || error?.message || (typeof error === 'string' ? error : null))
      .filter((message) => typeof message === 'string' && message.trim().length)
      .map((message) => translateServerMessage(message.trim()));

    if (messages.length) {
      return messages.join(' ');
    }
  }

  if (typeof responseJson === 'string') {
    return translateServerMessage(responseJson);
  }

  return translateServerMessage(responseJson?.message ||
    responseJson?.detail ||
    responseJson?.error) ||
    (xhr?.status && xhr?.statusText ? `${xhr.status} ${xhr.statusText}` : null) ||
    xhr?.statusText ||
    translate('RequestFailed');
}

function formatDuration(durationMilliseconds) {
  const seconds = Math.max(0, Number(durationMilliseconds || 0)) / 1000;
  return `${seconds.toFixed(1)}s`;
}

class ImportListSyncModal extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      isLoadingHistory: false,
      historyError: null,
      history: [],
      isPreviewing: false,
      previewError: null,
      preview: null
    };

    this._historyRequest = null;
    this._previewRequest = null;
    this._isFetchingHistory = false;
    this._isPreviewing = false;
  }

  componentDidMount() {
    if (this.props.isOpen) {
      this.fetchHistory();
    }
  }

  componentDidUpdate(prevProps) {
    if (!prevProps.isOpen && this.props.isOpen) {
      this.setState({ preview: null, previewError: null });
      this.fetchHistory();
    }

    if (prevProps.isOpen && !this.props.isOpen) {
      this.cancelRequests();
      this.setState({
        isLoadingHistory: false,
        historyError: null,
        isPreviewing: false,
        previewError: null,
        preview: null
      });
    }
  }

  componentWillUnmount() {
    this.cancelRequests();
  }

  cancelRequests = () => {
    const historyRequest = this._historyRequest;
    const previewRequest = this._previewRequest;

    this._historyRequest = null;
    this._previewRequest = null;
    this._isFetchingHistory = false;
    this._isPreviewing = false;

    historyRequest?.abortRequest();
    previewRequest?.abortRequest();
  };

  fetchHistory = (force = false) => {
    const { id } = this.props;

    if (this._isFetchingHistory && !force) {
      return;
    }

    if (this._isFetchingHistory && force) {
      const previousRequest = this._historyRequest;
      this._historyRequest = null;
      this._isFetchingHistory = false;
      previousRequest?.abortRequest();
    }

    this._isFetchingHistory = true;
    this.setState({ isLoadingHistory: true, historyError: null });

    const request = createAjaxRequest({
      url: `/importlist/${id}/history`,
      dataType: 'json'
    });
    this._historyRequest = request;

    request.request.done((history) => {
      if (this._historyRequest !== request) {
        return;
      }

      this._historyRequest = null;
      this._isFetchingHistory = false;
      this.setState({ history: history || [], isLoadingHistory: false });
    });

    request.request.fail((xhr) => {
      if (this._historyRequest !== request) {
        return;
      }

      this._historyRequest = null;
      this._isFetchingHistory = false;

      if (xhr?.aborted) {
        this.setState({ isLoadingHistory: false });
        return;
      }

      this.setState({ historyError: getErrorMessage(xhr), isLoadingHistory: false });
    });
  };

  onPreviewPress = () => {
    const { id } = this.props;

    if (this._isPreviewing || !this.props.isOpen) {
      return;
    }

    this._isPreviewing = true;
    this.setState({ isPreviewing: true, previewError: null, preview: null });

    const request = createAjaxRequest({
      url: `/importlist/${id}/preview`,
      method: 'POST',
      data: '{}',
      dataType: 'json'
    });
    this._previewRequest = request;

    request.request.done((preview) => {
      if (this._previewRequest !== request) {
        return;
      }

      this._previewRequest = null;
      this._isPreviewing = false;
      this.setState({ preview, isPreviewing: false });
      this.fetchHistory(true);
    });

    request.request.fail((xhr) => {
      if (this._previewRequest !== request) {
        return;
      }

      this._previewRequest = null;
      this._isPreviewing = false;

      if (xhr?.aborted) {
        this.setState({ isPreviewing: false });
        return;
      }

      this.setState({ previewError: getErrorMessage(xhr), isPreviewing: false });
      this.fetchHistory(true);
    });
  };

  renderCount(label, value) {
    return (
      <div className={styles.count} key={label}>
        <span>{translate(label)}</span>
        <strong>{value}</strong>
      </div>
    );
  }

  renderSyncSummary(sync) {
    if (!sync) {
      return null;
    }

    let summaryTitle = 'ImportListSyncHistory';
    if (sync.result === 'preview') {
      summaryTitle = 'PreviewComplete';
    } else if (sync.result === 'failed') {
      summaryTitle = 'PreviewFailed';
    }

    const counts = [
      ['ProcessedItems', sync.processedCount],
      ['NewItems', sync.newCount],
      ['ExistingItems', sync.existingCount],
      ['ChangedItems', sync.changedCount],
      ['ExcludedItems', sync.excludedCount],
      ['UnresolvedItems', sync.unresolvedCount],
      ['SearchEligibleItems', sync.searchEligibleCount]
    ];

    return (
      <div className={styles.summary}>
        <div className={styles.summaryTitle}>
          {translate(summaryTitle)}
          {sync.failureStage ? ` — ${translate('FailureStage')}: ${sync.failureStage}` : null}
        </div>
        <div className={styles.counts}>
          {counts.map(([label, value]) => this.renderCount(label, value))}
        </div>
      </div>
    );
  }

  renderHistoryRow(sync) {
    const resultLabels = {
      succeeded: 'SyncSucceeded',
      failed: 'SyncFailed',
      preview: 'PreviewComplete'
    };
    const resultLabel = resultLabels[sync.result] || 'SyncRunning';
    let historyState = null;
    if (sync.failureStage) {
      historyState = `${translate('FailureStage')}: ${sync.failureStage}`;
    } else if (sync.providerStateCommitted) {
      historyState = translate('ProviderStateCommitted');
    } else if (sync.dryRun) {
      historyState = translate('DryRunNoProviderCommit');
    }

    return (
      <div className={styles.historyRow} key={sync.id}>
        <div className={styles.historyStatus}>{translate(resultLabel)}</div>
        <div className={styles.historyDate}>{new Date(sync.startedAt).toLocaleString()}</div>
        <div className={styles.historyDuration}>{formatDuration(sync.durationMilliseconds)}</div>
        <div className={styles.historyCounts}>
          {translate('ProcessedItems')}: {sync.processedCount}, {translate('NewItems')}: {sync.newCount}, {translate('ChangedItems')}: {sync.changedCount}
        </div>
        <div className={styles.historyState}>
          {historyState}
        </div>
      </div>
    );
  }

  render() {
    const { name, isOpen, onModalClose } = this.props;
    const {
      isLoadingHistory,
      historyError,
      history,
      isPreviewing,
      previewError,
      preview
    } = this.state;

    return (
      <Modal
        isOpen={isOpen}
        onModalClose={onModalClose}
        size={sizes.LARGE}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>{translate('ImportListSyncHistory')}: {name}</ModalHeader>

          <ModalBody>
            <div className={styles.actions}>
              <Button
                kind={kinds.PRIMARY}
                isDisabled={isPreviewing}
                onPress={this.onPreviewPress}
              >
                {isPreviewing ? translate('PreviewingSync') : translate('PreviewNextSync')}
              </Button>
            </div>

            {previewError ? (
              <div className={styles.error} role="alert">
                <span>{previewError}</span>
                <Button
                  size={sizes.SMALL}
                  onPress={this.onPreviewPress}
                >
                  {translate('Retry')}
                </Button>
              </div>
            ) : null}
            {this.renderSyncSummary(preview)}

            <h3 className={styles.historyTitle}>{translate('History')}</h3>
            {isLoadingHistory ? <LoadingIndicator /> : null}
            {historyError ? (
              <div className={styles.error} role="alert">
                <span>{historyError}</span>
                <Button
                  size={sizes.SMALL}
                  isDisabled={isLoadingHistory}
                  onPress={() => this.fetchHistory()}
                >
                  {translate('Retry')}
                </Button>
              </div>
            ) : null}
            {!isLoadingHistory && !historyError && history.length === 0 ? (
              <div className={styles.empty}>{translate('NoImportListSyncHistory')}</div>
            ) : null}
            <div className={styles.history}>
              {history.map((sync) => this.renderHistoryRow(sync))}
            </div>
          </ModalBody>

          <ModalFooter>
            <Button onPress={onModalClose}>{translate('Close')}</Button>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

ImportListSyncModal.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ImportListSyncModal;
