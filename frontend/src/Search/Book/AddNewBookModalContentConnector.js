import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { addBook, resetAddState, setBookAddDefault } from 'Store/Actions/searchActions';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import AddNewBookModalContent from './AddNewBookModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { isExistingAuthor }) => isExistingAuthor,
    (state) => state.search,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.ui,
    createDimensionsSelector(),
    createSystemStatusSelector(),
    (isExistingAuthor, searchState, metadataProfiles, uiState, dimensions, systemStatus) => {
      const {
        isAdding,
        isQueued,
        addNotice,
        addError,
        addedMediaTypes,
        addFailedMediaType,
        bookDefaults
      } = searchState;

      const {
        settings,
        validationErrors,
        validationWarnings
      } = selectSettings(bookDefaults, {}, addError);

      return {
        isAdding,
        isQueued,
        addNotice,
        addError,
        addedMediaTypes,
        addFailedMediaType,
        defaultMediaType: uiState.item?.addNewDefaultMediaType,
        isSavingDefaultMediaType: uiState.isSaving,
        uiSaveError: uiState.saveError,
        showMetadataProfile: true,
        isSmallScreen: dimensions.isSmallScreen,
        validationErrors,
        validationWarnings,
        isWindows: systemStatus.isWindows,
        ...settings
      };
    }
  );
}

const mapDispatchToProps = {
  setBookAddDefault,
  addBook,
  resetAddState,
  setUISettingsValue,
  saveUISettings,
  showMessage
};

class AddNewBookModalContentConnector extends Component {

  constructor(props, context) {
    super(props, context);

    this._pendingDefaultSave = false;
  }

  componentDidMount() {
    this.props.resetAddState();
  }

  componentDidUpdate(prevProps) {
    if (this._pendingDefaultSave && prevProps.isSavingDefaultMediaType && !this.props.isSavingDefaultMediaType) {
      if (this.props.uiSaveError) {
        this.props.showMessage({
          id: `add-new-default-media-type-save-failed-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaveFailed',
          message: this.props.uiSaveError?.responseJSON?.message || 'Unable to save default media type',
          type: 'error',
          hideAfter: 8
        });
      } else {
        this.props.showMessage({
          id: `add-new-default-media-type-saved-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaved',
          message: 'Saved default media type',
          type: 'success',
          hideAfter: 5
        });
      }

      this._pendingDefaultSave = false;
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setBookAddDefault({ [name]: value });
  };

  onSetDefaultMediaType = (mediaType) => {
    this._pendingDefaultSave = true;
    this.props.setUISettingsValue({ name: 'addNewDefaultMediaType', value: mediaType });
    this.props.saveUISettings();
  };

  onAddBookPress = (searchForNewBook, mediaType) => {
    const {
      foreignBookId, // This is the provider-prefixed ID from search
      foreignAuthorId, // Provider-prefixed author ID (e.g., "hc:191785")
      book,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      monitor,
      audiobookMonitor,
      ebookMonitor,
      monitorNewItems,
      audiobookMonitorNewItems,
      ebookMonitorNewItems,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      metadataProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      tags,
      audiobookTags,
      ebookTags
    } = this.props;

    if (!foreignBookId || !String(foreignBookId).trim()) {
      this.props.showMessage({
        id: `add-book-missing-foreign-book-id-${Date.now()}`,
        name: 'AddBookMissingForeignBookId',
        message: 'Cannot add this book: missing upstream provider book/work ID',
        type: 'error',
        hideAfter: 10
      });
      return;
    }

    if (!foreignAuthorId || !String(foreignAuthorId).trim()) {
      this.props.showMessage({
        id: `add-book-missing-foreign-author-id-${Date.now()}`,
        name: 'AddBookMissingForeignAuthorId',
        message: 'Cannot add this book: missing upstream provider author ID',
        type: 'error',
        hideAfter: 10
      });
      return;
    }

    let selectedMediaType = 'audiobook';

    if (mediaType === 'both') {
      selectedMediaType = 'both';
    } else if (mediaType === 'ebook') {
      selectedMediaType = 'ebook';
    }

    const audiobookMonitorValue = audiobookMonitor?.value || monitor?.value;
    const ebookMonitorValue = ebookMonitor?.value || monitor?.value;
    const audiobookMonitorNewItemsValue = audiobookMonitorNewItems?.value || monitorNewItems?.value;
    const ebookMonitorNewItemsValue = ebookMonitorNewItems?.value || monitorNewItems?.value;
    const audiobookMetaId = audiobookMetadataProfileId?.value || metadataProfileId?.value || 1;
    const ebookMetaId = ebookMetadataProfileId?.value || metadataProfileId?.value || 2;
    const audiobookTagsValue = audiobookTags?.value || tags?.value;
    const ebookTagsValue = ebookTags?.value || tags?.value;
    const selectedMonitor = selectedMediaType === 'ebook' ? ebookMonitorValue : audiobookMonitorValue;
    const selectedMonitorNewItems = selectedMediaType === 'ebook' ? ebookMonitorNewItemsValue : audiobookMonitorNewItemsValue;
    const selectedMetadataProfileId = selectedMediaType === 'ebook' ? ebookMetaId : audiobookMetaId;
    const selectedTags = selectedMediaType === 'ebook' ? ebookTagsValue : audiobookTagsValue;

    const payload = {
      foreignBookId, // The search action expects this field name (e.g., "hc:495645")
      foreignAuthorId, // Provider ID for the author (e.g., "hc:191785")
      searchForNewBook: !!searchForNewBook,
      mediaType: selectedMediaType, // Pass mediaType for the search action to use (not sent to API)
      audiobookRootFolderPath: audiobookRootFolderPath?.value,
      ebookRootFolderPath: ebookRootFolderPath?.value,
      audiobookQualityProfileId: audiobookQualityProfileId?.value,
      ebookQualityProfileId: ebookQualityProfileId?.value,
      monitor: selectedMonitor,
      monitorNewItems: selectedMonitorNewItems,
      audiobookMonitor: audiobookMonitorValue,
      ebookMonitor: ebookMonitorValue,
      audiobookMonitorNewItems: audiobookMonitorNewItemsValue,
      ebookMonitorNewItems: ebookMonitorNewItemsValue,
      metadataProfileId: selectedMetadataProfileId,
      audiobookMetadataProfileId: audiobookMetaId,
      ebookMetadataProfileId: ebookMetaId,
      tags: selectedTags,
      audiobookTags: audiobookTagsValue,
      ebookTags: ebookTagsValue
    };

    if (book) {
      payload.book = book;
    }

    this.props.addBook(payload);
  };

  //
  // Render

  render() {
    // Pass through all props including folder from the parent
    return (
      <AddNewBookModalContent
        {...this.props}
        onInputChange={this.onInputChange}
        onAddBookPress={this.onAddBookPress}
        onSetDefaultMediaType={this.onSetDefaultMediaType}
      />
    );
  }
}

AddNewBookModalContentConnector.propTypes = {
  isExistingAuthor: PropTypes.bool.isRequired,
  isQueued: PropTypes.bool.isRequired,
  addNotice: PropTypes.string,
  addedMediaTypes: PropTypes.arrayOf(PropTypes.oneOf(['audiobook', 'ebook'])),
  addFailedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  defaultMediaType: PropTypes.string,
  isSavingDefaultMediaType: PropTypes.bool.isRequired,
  uiSaveError: PropTypes.object,
  foreignBookId: PropTypes.string,
  foreignAuthorId: PropTypes.string.isRequired,
  book: PropTypes.object,
  folder: PropTypes.string,
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  monitor: PropTypes.object.isRequired,
  audiobookMonitor: PropTypes.object,
  ebookMonitor: PropTypes.object,
  monitorNewItems: PropTypes.object.isRequired,
  audiobookMonitorNewItems: PropTypes.object,
  ebookMonitorNewItems: PropTypes.object,
  audiobookQualityProfileId: PropTypes.object,
  ebookQualityProfileId: PropTypes.object,
  metadataProfileId: PropTypes.object,
  audiobookMetadataProfileId: PropTypes.object,
  ebookMetadataProfileId: PropTypes.object,
  tags: PropTypes.object.isRequired,
  audiobookTags: PropTypes.object,
  ebookTags: PropTypes.object,
  onModalClose: PropTypes.func.isRequired,
  setBookAddDefault: PropTypes.func.isRequired,
  addBook: PropTypes.func.isRequired,
  resetAddState: PropTypes.func.isRequired,
  setUISettingsValue: PropTypes.func.isRequired,
  saveUISettings: PropTypes.func.isRequired,
  showMessage: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AddNewBookModalContentConnector);
