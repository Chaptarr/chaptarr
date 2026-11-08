import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import {
  cancelFetchInteractiveImportItems,
  clearInteractiveImport,
  fetchInteractiveImportItems,
  saveInteractiveImportItem,
  setInteractiveImportMode,
  setInteractiveImportSort,
  updateInteractiveImportItem } from 'Store/Actions/interactiveImportActions';
import { fetchMediaManagementSettings, saveMediaManagementSettingsFields } from 'Store/Actions/settingsActions';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import InteractiveImportModalContent from './InteractiveImportModalContent';

function createMapStateToProps() {
  return createSelector(
    createClientSideCollectionSelector('interactiveImport'),
    createSettingsSectionSelector('mediaManagement'),
    (interactiveImport, mediaManagementSettings) => {
      const importMode = interactiveImport.importMode === 'move' ? 'move' : 'copy';

      return {
        ...interactiveImport,
        importMode,
        mediaManagementSettings
      };
    }
  );
}

const mapDispatchToProps = {
  cancelFetchInteractiveImportItems,
  fetchInteractiveImportItems,
  setInteractiveImportSort,
  setInteractiveImportMode,
  clearInteractiveImport,
  updateInteractiveImportItem,
  saveInteractiveImportItem,
  executeCommand,
  fetchMediaManagementSettings,
  saveMediaManagementSettings: saveMediaManagementSettingsFields
};

class InteractiveImportModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      interactiveImportErrorMessage: null,
      pathFallbackSaveError: null,
      filterExistingFiles: props.filterExistingFiles,
      replaceExistingFiles: props.replaceExistingFiles
    };
  }

  componentDidMount() {
    const {
      authorId,
      bookFileIds,
      downloadId,
      folder,
      mediaType,
      mediaManagementSettings
    } = this.props;

    if (!mediaManagementSettings.isPopulated) {
      this.props.fetchMediaManagementSettings();
    }

    const {
      filterExistingFiles,
      replaceExistingFiles
    } = this.state;

    this.props.fetchInteractiveImportItems({
      authorId,
      bookFileIds,
      downloadId,
      folder,
      mediaType,
      filterExistingFiles,
      replaceExistingFiles
    });
  }

  componentDidUpdate(prevProps, prevState) {
    const {
      filterExistingFiles,
      replaceExistingFiles
    } = this.state;

    if (prevState.filterExistingFiles !== filterExistingFiles ||
        prevState.replaceExistingFiles !== replaceExistingFiles) {
      const {
        authorId,
        bookFileIds,
        downloadId,
        folder,
        mediaType
      } = this.props;

      this.props.fetchInteractiveImportItems({
        authorId,
        bookFileIds,
        downloadId,
        folder,
        mediaType,
        filterExistingFiles,
        replaceExistingFiles
      });
    }
  }

  componentWillUnmount() {
    this.props.cancelFetchInteractiveImportItems();
    this.props.clearInteractiveImport();
  }

  //
  // Listeners

  onSortPress = (sortKey, sortDirection) => {
    this.props.setInteractiveImportSort({ sortKey, sortDirection });
  };

  onFilterExistingFilesChange = (filterExistingFiles) => {
    this.setState({ filterExistingFiles });
  };

  onReplaceExistingFilesChange = (replaceExistingFiles) => {
    this.setState({ replaceExistingFiles });
  };

  onImportModeChange = (importMode) => {
    this.props.setInteractiveImportMode({ importMode });
  };

  refetchItems = () => {
    const {
      authorId,
      bookFileIds,
      downloadId,
      folder,
      mediaType
    } = this.props;

    const {
      filterExistingFiles,
      replaceExistingFiles
    } = this.state;

    this.props.fetchInteractiveImportItems({
      authorId,
      bookFileIds,
      downloadId,
      folder,
      mediaType,
      filterExistingFiles,
      replaceExistingFiles
    });
  };

  onPathFallbackChange = ({ value }) => {
    const { mediaManagementSettings } = this.props;

    if (!mediaManagementSettings.isPopulated || mediaManagementSettings.error || mediaManagementSettings.isFetching || mediaManagementSettings.isSaving ||
        (mediaManagementSettings.settings?.bookMatchingStrictness?.value === 'strict' && value)) {
      return;
    }

    const promise = this.props.saveMediaManagementSettings({
      usePathAsTagsFallback: value
    });

    if (promise && promise.done) {
      promise.done(() => {
        this.setState({ pathFallbackSaveError: null });
        this.refetchItems();
      }).fail((pathFallbackSaveError) => this.setState({ pathFallbackSaveError }));
    }
  };

  onImportSelectedPress = (selected, importMode, confirmedReplacementFiles) => {
    const files = [];
    const previewFiles = this.props.items.map((item) => {
      const {
        book
      } = item;

      return {
        path: item.path,
        bookId: book && book.id ? book.id : 0,
        downloadId: item.downloadId || this.props.downloadId
      };
    });
    let validationError = null;

    if (importMode === 'chooseImportMode') {
      this.setState({ interactiveImportErrorMessage: 'An import mode must be selected' });
      return;
    }

    _.forEach(this.props.items, (item) => {
      const isSelected = selected.indexOf(item.id) > -1;

      if (isSelected) {
        const {
          author,
          suggestedForeignAuthorId,
          suggestedAuthorName,
          suggestedForeignBookId,
          suggestedBookTitle,
          suggestedForeignEditionId,
          suggestedEditionTitle,
          book,
          editionId,
          quality,
          releaseGroup,
          indexerFlags,
          disableReleaseSwitching
        } = item;

        const hasLocalAuthor = !!(author && author.id);
        const hasSuggestedAuthor = !!suggestedForeignAuthorId;
        const hasAuthor = hasLocalAuthor || hasSuggestedAuthor;
        const hasBook = !!(book && book.id);
        const isLocalReady = !!(hasLocalAuthor && hasBook && editionId);
        const useMetadataSuggestion = !isLocalReady && hasSuggestedAuthor;

        if (!hasAuthor) {
          validationError = 'Author must be selected (or suggested) for each selected file';
          return false;
        }

        // A metadata suggestion remains selectable when only the author resolved locally.
        if (hasLocalAuthor && !hasBook && !useMetadataSuggestion) {
          validationError = 'Book must be selected for each selected file';
          return false;
        }

        if (hasLocalAuthor && hasBook && !editionId && !useMetadataSuggestion) {
          validationError = 'Edition must be selected for each selected file';
          return false;
        }

        if (!quality) {
          validationError = 'Quality must be chosen for each selected file';
          return false;
        }

        files.push({
          path: item.path,
          authorId: !useMetadataSuggestion && hasLocalAuthor ? author.id : 0,
          bookId: !useMetadataSuggestion && hasBook ? book.id : 0,
          editionId: !useMetadataSuggestion && hasBook && editionId ? editionId : 0,
          foreignAuthorId: useMetadataSuggestion ? suggestedForeignAuthorId : undefined,
          foreignAuthorName: useMetadataSuggestion ? suggestedAuthorName : undefined,
          foreignBookId: useMetadataSuggestion ? suggestedForeignBookId : undefined,
          foreignBookTitle: useMetadataSuggestion ? suggestedBookTitle : undefined,
          foreignEditionId: useMetadataSuggestion ? suggestedForeignEditionId : undefined,
          foreignEditionTitle: useMetadataSuggestion ? suggestedEditionTitle : undefined,
          selectionSource: useMetadataSuggestion ? 'userMetadataSuggestion' : 'automatic',
          quality,
          releaseGroup,
          indexerFlags,
          ...(item.audioProductionOverrideChanged ? { audioProductionOverride: item.audioProductionOverride } : {}),
          downloadId: item.downloadId || this.props.downloadId,
          disableReleaseSwitching
        });
      }
    });

    if (validationError) {
      this.setState({ interactiveImportErrorMessage: validationError });
      return;
    }

    if (!files.length) {
      return;
    }

    this.setState({ interactiveImportErrorMessage: null });

    this.props.executeCommand({
      name: commandNames.INTERACTIVE_IMPORT,
      files,
      previewFiles,
      importMode,
      replaceExistingFiles: this.state.replaceExistingFiles,
      confirmedReplacementFiles,
      commandFinished: this.props.onImportComplete
    });

    this.props.onModalClose();
  };

  //
  // Render

  render() {
    const {
      interactiveImportErrorMessage,
      filterExistingFiles,
      replaceExistingFiles
    } = this.state;

    return (
      <InteractiveImportModalContent
        {...this.props}
        pathFallbackSaveError={this.state.pathFallbackSaveError}
        interactiveImportErrorMessage={interactiveImportErrorMessage}
        filterExistingFiles={filterExistingFiles}
        replaceExistingFiles={replaceExistingFiles}
        onSortPress={this.onSortPress}
        onFilterExistingFilesChange={this.onFilterExistingFilesChange}
        onReplaceExistingFilesChange={this.onReplaceExistingFilesChange}
        onImportModeChange={this.onImportModeChange}
        onPathFallbackChange={this.onPathFallbackChange}
        onRetrySettings={() => this.props.fetchMediaManagementSettings()}
        onImportSelectedPress={this.onImportSelectedPress}
      />
    );
  }
}

InteractiveImportModalContentConnector.propTypes = {
  authorId: PropTypes.number,
  bookFileIds: PropTypes.arrayOf(PropTypes.number),
  downloadId: PropTypes.string,
  folder: PropTypes.string,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  filterExistingFiles: PropTypes.bool.isRequired,
  replaceExistingFiles: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  cancelFetchInteractiveImportItems: PropTypes.func.isRequired,
  fetchInteractiveImportItems: PropTypes.func.isRequired,
  setInteractiveImportSort: PropTypes.func.isRequired,
  clearInteractiveImport: PropTypes.func.isRequired,
  setInteractiveImportMode: PropTypes.func.isRequired,
  updateInteractiveImportItem: PropTypes.func.isRequired,
  mediaManagementSettings: PropTypes.object.isRequired,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  onImportComplete: PropTypes.func,
  onModalClose: PropTypes.func.isRequired
};

InteractiveImportModalContentConnector.defaultProps = {
  authorId: 0,
  filterExistingFiles: true,
  replaceExistingFiles: false
};

export default connect(createMapStateToProps, mapDispatchToProps)(InteractiveImportModalContentConnector);
