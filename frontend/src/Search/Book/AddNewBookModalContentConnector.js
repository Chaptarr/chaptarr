import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { fetchAuthor } from 'Store/Actions/authorActions';
import { addBook, bookMonitoringDefaults, resetAddState, setBookAddDefault } from 'Store/Actions/searchActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import { saveUISettings, setUISettingsValue } from 'Store/Actions/settingsActions';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createExistingAuthorSelector from 'Store/Selectors/createExistingAuthorSelector';
import createRootFolderDefaultsSelector, { applyRootFolderMediaDefaults, getRootDerivedDefaultsForMediaType, rootDerivedAuthorDefaults } from 'Store/Selectors/createRootFolderDefaultsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import AddNewBookModalContent from './AddNewBookModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { authorId }) => authorId,
    (state) => state.authors,
    (state) => state.search,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.qualityProfiles,
    (state) => state.tags.items,
    (state) => state.settings.ui,
    (state) => state.settings.rootFolders,
    createRootFolderDefaultsSelector(),
    createExistingAuthorSelector(),
    createDimensionsSelector(),
    createSystemStatusSelector(),
    (authorId, authorsState, searchState, metadataProfiles, qualityProfiles, tagList, uiState, rootFoldersState, rootFolderDefaults, existingAuthor, dimensions, systemStatus) => {
      const {
        isAdding,
        isQueued,
        addNotice,
        addError,
        addedMediaTypes,
        addFailedMediaType,
        bookDefaults
      } = searchState;

      const defaults = { ...bookDefaults };
      for (const media of ['audiobook', 'ebook']) {
        if (existingAuthor?.[`${media}RootFolderPath`]) {
          for (const field of ['RootFolderPath', 'Monitored', 'MonitorNewItems', 'QualityProfileId', 'MetadataProfileId', 'Tags']) {
            defaults[`${media}${field}`] = existingAuthor[`${media}${field}`];
          }
        }
      }

      const {
        settings,
        validationErrors,
        validationWarnings
      } = selectSettings(defaults, {}, addError);

      for (const media of ['audiobook', 'ebook']) {
        const rootKey = `${media}RootFolderPath`;
        if (!settings[rootKey]?.value) {
          settings[rootKey] = { value: existingAuthor?.[rootKey] || rootFolderDefaults[rootKey] };
        }
      }
      applyRootFolderMediaDefaults(settings, rootFoldersState.isPopulated ? rootFoldersState.items : []);

      return {
        rootFoldersPopulated: rootFoldersState.isPopulated,
        rootFoldersError: rootFoldersState.error,
        existingAuthor,
        isLoadingAuthor: authorId > 0 && !existingAuthor,
        authorLoadError: authorsState.error,
        qualityProfiles: qualityProfiles.items,
        metadataProfiles: metadataProfiles.items,
        tagList,
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
  fetchAuthor,
  fetchRootFolders,
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
    this.props.setBookAddDefault({ ...rootDerivedAuthorDefaults, ...bookMonitoringDefaults });
    if (this.props.authorId > 0 && !this.props.existingAuthor) {
      this.props.fetchAuthor({ id: this.props.authorId });
    }
    if (!this.props.rootFoldersPopulated) {
      this.props.fetchRootFolders();
    }
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
    if ((name === 'audiobookMonitor' || name === 'ebookMonitor') && this.props.existingAuthor?.syncMonitoredAcrossFormats) {
      this.props.setBookAddDefault({ audiobookMonitor: value, ebookMonitor: value });
      return;
    }
    const defaults = name.endsWith('RootFolderPath') ? getRootDerivedDefaultsForMediaType(name.startsWith('ebook') ? 'ebook' : 'audiobook') : {};
    for (const key of Object.keys(defaults)) {
      if (key.endsWith('Monitored') || key.endsWith('Monitor') || key.endsWith('MonitorNewItems')) {
        delete defaults[key];
      }
    }
    this.props.setBookAddDefault({ ...defaults, [name]: value });
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
      audiobookMonitored,
      ebookMonitored,
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
    const audiobookMetaId = audiobookMetadataProfileId?.value ?? metadataProfileId?.value;
    const ebookMetaId = ebookMetadataProfileId?.value ?? metadataProfileId?.value;
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
      audiobookMonitored: audiobookMonitored?.value !== false,
      ebookMonitored: ebookMonitored?.value !== false,
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
  rootFoldersPopulated: PropTypes.bool.isRequired,
  existingAuthor: PropTypes.object,
  fetchRootFolders: PropTypes.func.isRequired,
  isQueued: PropTypes.bool.isRequired,
  addNotice: PropTypes.string,
  addedMediaTypes: PropTypes.arrayOf(PropTypes.oneOf(['audiobook', 'ebook'])),
  addFailedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  defaultMediaType: PropTypes.string,
  isSavingDefaultMediaType: PropTypes.bool.isRequired,
  uiSaveError: PropTypes.object,
  authorId: PropTypes.number,
  fetchAuthor: PropTypes.func.isRequired,
  foreignBookId: PropTypes.string,
  foreignAuthorId: PropTypes.string.isRequired,
  book: PropTypes.object,
  folder: PropTypes.string,
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  audiobookMonitored: PropTypes.object,
  ebookMonitored: PropTypes.object,
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
