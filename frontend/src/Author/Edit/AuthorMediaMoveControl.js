import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Tooltip from 'Components/Tooltip/Tooltip';
import { kinds, tooltipPositions } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import translateServerMessage from 'Utilities/String/translateServerMessage';
import styles from './AuthorMediaMoveControl.css';

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

class AuthorMediaMoveControl extends Component {

  state = {
    isOpen: false,
    isLoading: false,
    isStarting: false,
    error: null,
    preview: null,
    commandId: null
  };

  onPreviewPress = () => {
    const {
      authorId,
      format,
      destinationPath
    } = this.props;

    this.setState({
      isOpen: true,
      isLoading: true,
      error: null,
      preview: null,
      commandId: null
    });

    createAjaxRequest({
      url: '/author/media-move/preview',
      method: 'POST',
      data: JSON.stringify({
        id: authorId,
        format,
        destinationPath
      }),
      dataType: 'json'
    }).request
      .done((preview) => {
        this.setState({
          isLoading: false,
          preview
        });
      })
      .fail((xhr) => {
        this.setState({
          isLoading: false,
          error: getErrorMessage(xhr)
        });
      });
  };

  onStartPress = () => {
    const {
      authorId,
      format,
      destinationPath
    } = this.props;
    const { preview } = this.state;

    this.setState({ isStarting: true, error: null });

    createAjaxRequest({
      url: '/author/media-move/start',
      method: 'POST',
      data: JSON.stringify({
        id: authorId,
        format,
        destinationPath,
        previewToken: preview.previewToken
      }),
      dataType: 'json'
    }).request
      .done((command) => {
        this.setState({
          isStarting: false,
          commandId: command.id
        });
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
      isOpen: false,
      isLoading: false,
      isStarting: false,
      error: null,
      preview: null,
      commandId: null
    });
  };

  render() {
    const {
      format,
      destinationPath
    } = this.props;

    const {
      isOpen,
      isLoading,
      isStarting,
      error,
      preview,
      commandId
    } = this.state;

    const formatLabel = format === 'ebook' ? translate('Ebooks') : translate('Audiobooks');
    const actionLabel = format === 'ebook' ? translate('MoveExistingEbooks') : translate('MoveExistingAudiobooks');
    const visibleFiles = preview?.files?.slice(0, 8) || [];
    const movableMediaCount = preview?.files?.filter((file) => file.fileType === 'media' && file.status !== 'missing' && file.status !== 'conflict').length || 0;

    return (
      <>
        <Tooltip
          anchor={
            <Button
              className={styles.moveButton}
              title={translate('MediaMoveButtonTooltip')}
              isDisabled={!destinationPath}
              onPress={this.onPreviewPress}
            >
              {actionLabel}
            </Button>
          }
          tooltip={translate('MediaMoveButtonTooltip')}
          kind={kinds.INVERSE}
          position={tooltipPositions.TOP}
        />

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
              {translate('MoveExistingMediaTitle', { format: formatLabel })}
            </ModalHeader>

            <ModalBody>
              <p className={styles.spaceSummary}>{translate('MediaMoveDialogHelp')}</p>
              <p className={styles.spaceSummary}>{translate('MediaMoveAdminHelp')}</p>
              <details>
                <summary>{translate('MediaMoveFailureTitle')}</summary>
                <p className={styles.spaceSummary}>{translate('MediaMoveFailureHelp')}</p>
              </details>

              {isLoading && <div>{translate('LoadingMovePreview')}</div>}

              {
                preview &&
                  <>
                    <div className={styles.pathSummary}>
                      <div>
                        <span className={styles.pathLabel}>{translate('MoveFrom')}</span>
                        <code>{preview.sourcePath}</code>
                      </div>
                      <div>
                        <span className={styles.pathLabel}>{translate('MoveTo')}</span>
                        <code>{preview.destinationPath}</code>
                      </div>
                    </div>

                    <div className={styles.summary}>
                      <strong>{preview.mediaFileCount} {formatLabel.toLowerCase()}</strong>
                      <span>{preview.sidecarFileCount} {translate('SidecarFiles')}</span>
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

                    {
                      visibleFiles.length > 0 &&
                        <div className={styles.fileList}>
                          {visibleFiles.map((file) => (
                            <div key={`${file.fileType}-${file.sourcePath}`} className={styles.fileRow}>
                              <span className={styles.fileStatus}>{translate(`MediaMoveStatus${file.status}`)}</span>
                              <code>{file.sourcePath}</code>
                              <span className={styles.pathArrow} aria-hidden="true">{'›'}</span>
                              <code>{file.destinationPath}</code>
                            </div>
                          ))}
                          {
                            preview.files.length > visibleFiles.length &&
                              <div className={styles.moreFiles}>
                                {translate('AndMoreMoveFiles', { count: preview.files.length - visibleFiles.length })}
                              </div>
                          }
                        </div>
                    }
                  </>
              }

              {error && <div className={styles.error} role="alert">{error}</div>}

              {
                commandId &&
                  <div className={styles.queued} role="status">
                    {translate('MediaMoveQueued', { commandId })}
                  </div>
              }
            </ModalBody>

            <ModalFooter>
              <Button onPress={this.onModalClose}>
                {translate('Close')}
              </Button>
              {
                preview && !commandId &&
                  <SpinnerButton
                    isSpinning={isStarting}
                    isDisabled={!preview.canMove || isStarting}
                    onPress={this.onStartPress}
                  >
                    {format === 'ebook' ? translate('MoveEbookFiles', { count: movableMediaCount }) : translate('MoveAudiobookFiles', { count: movableMediaCount })}
                  </SpinnerButton>
              }
            </ModalFooter>
          </ModalContent>
        </Modal>
      </>
    );
  }
}

AuthorMediaMoveControl.propTypes = {
  authorId: PropTypes.number.isRequired,
  format: PropTypes.string.isRequired,
  destinationPath: PropTypes.string
};

export default AuthorMediaMoveControl;
