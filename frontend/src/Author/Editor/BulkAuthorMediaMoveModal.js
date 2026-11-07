import PropTypes from 'prop-types';
import React, { Component } from 'react';
import styles from 'Author/Edit/AuthorMediaMoveControl.css';
import RootFolderSelectInputConnector from 'Components/Form/RootFolderSelectInputConnector';
import SelectInput from 'Components/Form/SelectInput';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';

function getErrorMessage(xhr) {
  return translateServerMessage(xhr.responseJSON?.message || xhr.responseJSON?.error || translate('AuthorMediaMoveRequestFailed'));
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = units[0];

  for (let i = 1; value >= 1024 && i < units.length; i++) {
    value /= 1024;
    unit = units[i];
  }

  return `${value.toFixed(1)} ${unit}`;
}

class BulkAuthorMediaMoveModal extends Component {

  state = {
    format: 'audiobook',
    destinationRootPath: '',
    isLoading: false,
    isStarting: false,
    error: null,
    preview: null,
    commandId: null
  };

  onInputChange = ({ name, value }) => {
    this.setState({
      [name]: value,
      preview: null,
      error: null
    });
  };

  onPreviewPress = () => {
    const {
      authorIds
    } = this.props;
    const {
      format,
      destinationRootPath
    } = this.state;

    this.setState({ isLoading: true, error: null, preview: null, commandId: null });

    createAjaxRequest({
      url: '/author/media-move/bulk/preview',
      method: 'POST',
      data: JSON.stringify({
        authorIds,
        format,
        destinationRootPath
      }),
      dataType: 'json'
    }).request
      .done((preview) => {
        this.setState({ isLoading: false, preview });
      })
      .fail((xhr) => {
        this.setState({ isLoading: false, error: getErrorMessage(xhr) });
      });
  };

  onStartPress = () => {
    const {
      authorIds
    } = this.props;
    const {
      format,
      destinationRootPath,
      preview
    } = this.state;

    this.setState({ isStarting: true, error: null });

    createAjaxRequest({
      url: '/author/media-move/bulk/start',
      method: 'POST',
      data: JSON.stringify({
        authorIds,
        format,
        destinationRootPath,
        previewToken: preview.previewToken
      }),
      dataType: 'json'
    }).request
      .done((command) => {
        this.setState({ isStarting: false, commandId: command.id });
      })
      .fail((xhr) => {
        this.setState({
          isStarting: false,
          error: getErrorMessage(xhr),
          preview: xhr.responseJSON?.preview || preview
        });
      });
  };

  onModalClose = () => {
    this.setState({
      isLoading: false,
      isStarting: false,
      error: null,
      preview: null,
      commandId: null
    });
    this.props.onModalClose();
  };

  render() {
    const {
      isOpen,
      authorIds
    } = this.props;
    const {
      format,
      destinationRootPath,
      isLoading,
      isStarting,
      error,
      preview,
      commandId
    } = this.state;

    const formatLabel = format === 'ebook' ? translate('Ebooks') : translate('Audiobooks');
    const exceedsAuthorLimit = authorIds.length > 1000;
    const formatOptions = [
      { key: 'ebook', value: translate('Ebooks') },
      { key: 'audiobook', value: translate('Audiobooks') }
    ];

    return (
      <Modal
        isOpen={isOpen}
        closeOnBackgroundClick={false}
        onModalClose={this.onModalClose}
      >
        <ModalContent
          showCloseButton={true}
          onModalClose={this.onModalClose}
        >
          <ModalHeader>
            {translate('BulkMediaMoveTitle', { format: formatLabel })}
          </ModalHeader>

          <ModalBody>
            <p>{translate('BulkMediaMoveIntro')}</p>

            <div className={styles.pathSummary}>
              <div>
                <span className={styles.pathLabel}>{translate('BulkMediaMoveFormat')}</span>
                <SelectInput
                  name="format"
                  value={format}
                  values={formatOptions}
                  onChange={this.onInputChange}
                />
              </div>
              <div>
                <span className={styles.pathLabel}>{translate('BulkMediaMoveDestinationRoot')}</span>
                <RootFolderSelectInputConnector
                  name="destinationRootPath"
                  value={destinationRootPath}
                  isDisabled={isLoading || isStarting}
                  selectedValueOptions={{ includeFreeSpace: true }}
                  onChange={this.onInputChange}
                />
              </div>
            </div>

            <div className={styles.summary}>
              <span>{translate('SelectedCountAuthorsSelectedInterp', [authorIds.length])}</span>
            </div>

            <p className={styles.spaceSummary}>{translate('BulkMediaMoveDatabaseHelp')}</p>
            <p className={styles.spaceSummary}>{translate('BulkMediaMoveAdminHelp')}</p>
            {
              exceedsAuthorLimit &&
                <div className={styles.conflict} role="alert">
                  {translate('BulkMediaMoveLimitHelp')}
                </div>
            }
            <details>
              <summary>{translate('BulkMediaMoveDetailsTitle')}</summary>
              <p className={styles.spaceSummary}>{translate('BulkMediaMoveDetailsHelp')}</p>
              <p className={styles.spaceSummary}>{translate('BulkMediaMoveFailureHelp')}</p>
            </details>

            {isLoading && <div>{translate('LoadingMovePreview')}</div>}

            {
              preview &&
                <>
                  <p className={styles.spaceSummary}>{translate('BulkMediaMovePreviewHelp')}</p>

                  <div className={styles.pathSummary}>
                    <div>
                      <span className={styles.pathLabel}>{translate('BulkMediaMoveDestinationRoot')}</span>
                      <code>{preview.destinationRootPath}</code>
                    </div>
                  </div>

                  <div className={styles.summary}>
                    <strong>{translate('BulkMediaMoveSummary', {
                      authors: preview.authorCount,
                      media: preview.mediaFileCount,
                      format: formatLabel,
                      sidecars: preview.sidecarFileCount
                    })}</strong>
                    <span>{translate('MoveTotalSize')}: {formatBytes(preview.totalSize)}</span>
                    <span>{preview.missingFileCount} {translate('MissingFiles')}</span>
                  </div>

                  {
                    preview.requiredCopyBytes > 0 &&
                      <p className={styles.spaceSummary}>
                        {translate('MediaMoveCopySpaceHelpText', {
                          required: formatBytes(preview.requiredCopyBytes),
                          available: preview.availableSpace == null ? translate('Unknown') : formatBytes(preview.availableSpace)
                        })}
                      </p>
                  }

                  {
                    (preview.conflicts.length > 0 || preview.warnings.length > 0) &&
                      <div className={styles.notices}>
                        {preview.conflicts.map((conflict) => (
                          <div
                            key={conflict}
                            className={styles.conflict}
                            role="alert"
                          >
                            {conflict}
                          </div>
                        ))}
                        {preview.warnings.map((warning) => (
                          <div key={warning} className={styles.warning}>
                            {warning}
                          </div>
                        ))}
                      </div>
                  }

                  <div className={styles.fileList}>
                    {preview.authors.map((author) => (
                      <section key={author.authorId} className={styles.authorGroup}>
                        <strong>{author.authorName}</strong>
                        <code>{author.destinationPath}</code>
                        <span>{author.mediaFileCount} {formatLabel.toLowerCase()}, {author.sidecarFileCount} {translate('SidecarFiles')}</span>
                        {author.files.slice(0, 3).map((file) => (
                          <div key={`${file.fileType}-${file.sourcePath}`} className={styles.fileRow}>
                            <span className={styles.fileStatus}>{translate(`MediaMoveStatus${file.status}`)}</span>
                            <code>{file.sourcePath}</code>
                            <span aria-hidden="true">{'→'}</span>
                            <code>{file.destinationPath}</code>
                          </div>
                        ))}
                        {author.files.length > 3 &&
                          <div className={styles.moreFiles}>
                            {translate('AndMoreMoveFiles', { count: author.files.length - 3 })}
                          </div>
                        }
                      </section>
                    ))}
                  </div>
                </>
            }

            {error && <div className={styles.error} role="alert">{error}</div>}

            {
              commandId &&
                <div className={styles.queued} role="status">
                  {translate('BulkMediaMoveQueued', { commandId })}
                </div>
            }
          </ModalBody>

          <ModalFooter>
            <Button onPress={this.onModalClose}>
              {translate('Close')}
            </Button>
            {
              !preview &&
                <SpinnerButton
                  isSpinning={isLoading}
                  isDisabled={isLoading || !destinationRootPath || authorIds.length === 0 || exceedsAuthorLimit}
                  onPress={this.onPreviewPress}
                >
                  {translate('BulkMediaMovePreview')}
                </SpinnerButton>
            }
            {
              preview && !commandId &&
                <SpinnerButton
                  isSpinning={isStarting}
                  isDisabled={!preview.canMove || isStarting}
                  onPress={this.onStartPress}
                >
                  {translate('BulkMediaMoveStart')}
                </SpinnerButton>
            }
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

BulkAuthorMediaMoveModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  authorIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default BulkAuthorMediaMoveModal;
