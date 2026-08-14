import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import OrganizePreviewRow from './OrganizePreviewRow';
import styles from './OrganizePreviewModalContent.css';

function getValue(allSelected, allUnselected) {
  if (allSelected) {
    return true;
  } else if (allUnselected) {
    return false;
  }

  return null;
}

class OrganizePreviewModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {}
    };
  }

  //
  // Control

  getSelectedIds = () => {
    const eligibleIds = new Set(
      this.props.items
        .filter((item) => item.canOrganize !== false)
        .map((item) => item.bookFileId)
    );
    return getSelectedIds(this.state.selectedState).filter((id) => eligibleIds.has(id));
  };

  //
  // Listeners

  onSelectAllChange = ({ value }) => {
    this.setState(selectAll(this.state.selectedState, value));
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    this.setState((state) => {
      return toggleSelected(state, this.props.items, id, value, shiftKey);
    });
  };

  onOrganizePress = () => {
    this.props.onOrganizePress(this.getSelectedIds());
  };

  onMoveToCanonicalAuthorFolderChange = ({ value }) => {
    this.props.onMoveToCanonicalAuthorFolderChange({ value });
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      items,
      trackFormat,
      renameBooksEnabled,
      canMoveToCanonicalAuthorFolder,
      moveToCanonicalAuthorFolder,
      onModalClose
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState
    } = this.state;

    const selectAllValue = getValue(allSelected, allUnselected);
    const selectedIds = this.getSelectedIds();
    const selectedIdSet = new Set(selectedIds);
    const filesByEdition = items.reduce((result, item) => {
      if (item.canOrganize === false) {
        return result;
      }

      const editionFiles = result.get(item.editionId) || [];
      editionFiles.push(item);
      result.set(item.editionId, editionFiles);
      return result;
    }, new Map());
    const hasPartiallySelectedEdition = moveToCanonicalAuthorFolder &&
      Array.from(filesByEdition.values()).some((editionFiles) => {
        const selectedCount = editionFiles.filter((item) => selectedIdSet.has(item.bookFileId)).length;
        return selectedCount > 0 && selectedCount < editionFiles.length;
      });

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('OrganizeAndRename')}
        </ModalHeader>

        <ModalBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && error &&
              <div>
                {translate('ErrorLoadingPreviews')}
              </div>
          }

          {
            !isFetching && isPopulated && canMoveToCanonicalAuthorFolder &&
              <label className={styles.canonicalAuthorFolderOption}>
                <span className={styles.canonicalAuthorFolderLabel}>
                  {translate('MoveSelectedFilesToCanonicalAuthorFolder')}
                </span>

                <CheckInput
                  containerClassName={styles.canonicalAuthorFolderInputContainer}
                  className={styles.canonicalAuthorFolderInput}
                  name="moveToCanonicalAuthorFolder"
                  value={moveToCanonicalAuthorFolder}
                  onChange={this.onMoveToCanonicalAuthorFolderChange}
                />
              </label>
          }

          {
            !isFetching && isPopulated && !items.length &&
              <div>
                {translate('SuccessMyWorkIsDoneNoFilesToRename')}
              </div>
          }

          {
            !isFetching && isPopulated && !!items.length &&
              <div>
                <Alert>
                  <div>
                    {renameBooksEnabled ? translate('OrganizeNamingPatternLabel') : translate('OrganizeNamingPatternDisabledLabel')}
                    <span className={styles.trackFormat}>
                      {trackFormat}
                    </span>
                  </div>
                  {
                    !renameBooksEnabled &&
                      <div>
                        {translate('OrganizeEnableRenameBooksHint')}
                      </div>
                  }
                </Alert>

                {
                  hasPartiallySelectedEdition &&
                    <Alert kind={kinds.WARNING}>
                      {translate('CanonicalAuthorFolderPartialEditionSelectionWarning')}
                    </Alert>
                }

                <div className={styles.previews}>
                  {
                    items.map((item) => {
                      return (
                        <OrganizePreviewRow
                          key={item.bookFileId}
                          id={item.bookFileId}
                          existingPath={item.existingPath}
                          newPath={item.newPath}
                          canOrganize={item.canOrganize !== false}
                          reason={item.reason}
                          isSelected={item.canOrganize !== false && selectedState[item.bookFileId]}
                          onSelectedChange={this.onSelectedChange}
                        />
                      );
                    })
                  }
                </div>
              </div>
          }
        </ModalBody>

        <ModalFooter>
          {
            isPopulated && !!items.length &&
              <CheckInput
                className={styles.selectAllInput}
                containerClassName={styles.selectAllInputContainer}
                name="selectAll"
                value={selectAllValue}
                onChange={this.onSelectAllChange}
              />
          }

          <Button
            onPress={onModalClose}
          >
            {translate('Cancel')}
          </Button>

          <Button
            kind={kinds.PRIMARY}
            isDisabled={!selectedIds.length}
            onPress={this.onOrganizePress}
          >
            {translate('Organize')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

OrganizePreviewModalContent.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  path: PropTypes.string.isRequired,
  trackFormat: PropTypes.string,
  renameBooksEnabled: PropTypes.bool,
  canMoveToCanonicalAuthorFolder: PropTypes.bool.isRequired,
  moveToCanonicalAuthorFolder: PropTypes.bool.isRequired,
  onMoveToCanonicalAuthorFolderChange: PropTypes.func.isRequired,
  onOrganizePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default OrganizePreviewModalContent;
