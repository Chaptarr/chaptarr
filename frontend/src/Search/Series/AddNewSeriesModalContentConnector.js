import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { showMessage } from 'Store/Actions/appActions';
import { setAuthorAddDefault } from 'Store/Actions/searchActions';
import {
  fetchRootFolders,
  saveUISettings,
  setUISettingsValue
} from 'Store/Actions/settingsActions';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createRootFolderDefaultsSelector from 'Store/Selectors/createRootFolderDefaultsSelector';
import createSystemStatusSelector from 'Store/Selectors/createSystemStatusSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import { normalizeMonitorNewItemsOption } from 'Utilities/Author/monitorNewItemsOptions';
import { normalizeMonitorOption, resolveMonitorOption } from 'Utilities/Author/monitorOptions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import AddNewSeriesModalContent from './AddNewSeriesModalContent';

function createMapStateToProps() {
  return createSelector(
    createDimensionsSelector(),
    (state) => state.search,
    (state) => state.settings.rootFolders,
    (state) => state.settings.qualityProfiles,
    (state) => state.settings.metadataProfiles,
    (state) => state.settings.ui,
    createRootFolderDefaultsSelector(),
    createSystemStatusSelector(),
    (dimensions, search, rootFoldersState, qualityProfiles, metadataProfiles, uiState, rootFolderDefaults, systemStatus) => {
      const {
        isAdding,
        addError,
        authorDefaults
      } = search;

      const {
        settings,
        validationErrors,
        validationWarnings
      } = selectSettings(authorDefaults, {}, addError);

      // Defensive defaults for legacy/empty persisted state
      if (!settings.monitor) {
        settings.monitor = {
          value: 'none',
          errors: [],
          warnings: []
        };
      }

      if (!settings.monitorNewItems) {
        settings.monitorNewItems = {
          value: 'none',
          errors: [],
          warnings: []
        };
      }

      if (!settings.tags) {
        settings.tags = {
          value: [],
          errors: [],
          warnings: []
        };
      }

      if (rootFoldersState.isPopulated) {
        if (!settings.audiobookRootFolderPath || !settings.audiobookRootFolderPath.value) {
          settings.audiobookRootFolderPath = {
            ...(settings.audiobookRootFolderPath || {}),
            value: rootFolderDefaults.audiobookRootFolderPath
          };
        }

        if (!settings.ebookRootFolderPath || !settings.ebookRootFolderPath.value) {
          settings.ebookRootFolderPath = {
            ...(settings.ebookRootFolderPath || {}),
            value: rootFolderDefaults.ebookRootFolderPath
          };
        }

        const rootFolders = rootFoldersState.items || [];

        const audiobookRoot = settings.audiobookRootFolderPath?.value;
        const ebookRoot = settings.ebookRootFolderPath?.value;

        const audiobookRootFolder = rootFolders.find((f) => f.path === audiobookRoot);
        const ebookRootFolder = rootFolders.find((f) => f.path === ebookRoot);

        if (!settings.audiobookMonitor && audiobookRootFolder) {
          settings.audiobookMonitor = {
            ...(settings.audiobookMonitor || {}),
            value: resolveMonitorOption(
              audiobookRootFolder.audiobookMonitorExistingMode,
              audiobookRootFolder.audiobookMonitorExistingBooks
            )
          };
        }

        if (!settings.audiobookMonitored && audiobookRootFolder) {
          settings.audiobookMonitored = {
            value: audiobookRootFolder.audiobookMonitored !== false
          };
        }

        if (!settings.audiobookMonitorNewItems && audiobookRootFolder) {
          settings.audiobookMonitorNewItems = {
            ...(settings.audiobookMonitorNewItems || {}),
            value: normalizeMonitorNewItemsOption(audiobookRootFolder.audiobookMonitorNewItems)
          };
        }

        if (!settings.ebookMonitor && ebookRootFolder) {
          settings.ebookMonitor = {
            ...(settings.ebookMonitor || {}),
            value: resolveMonitorOption(
              ebookRootFolder.ebookMonitorExistingMode,
              ebookRootFolder.ebookMonitorExistingBooks
            )
          };
        }

        if (!settings.ebookMonitored && ebookRootFolder) {
          settings.ebookMonitored = {
            value: ebookRootFolder.ebookMonitored !== false
          };
        }

        if (!settings.ebookMonitorNewItems && ebookRootFolder) {
          settings.ebookMonitorNewItems = {
            ...(settings.ebookMonitorNewItems || {}),
            value: normalizeMonitorNewItemsOption(ebookRootFolder.ebookMonitorNewItems)
          };
        }

        const audiobookQualityFromRootFolder = audiobookRootFolder?.audiobookQualityProfileId || audiobookRootFolder?.audiobook?.qualityProfileId;
        const audiobookMetadataFromRootFolder = audiobookRootFolder?.audiobookMetadataProfileId || audiobookRootFolder?.audiobook?.metadataProfileId;
        const ebookQualityFromRootFolder = ebookRootFolder?.ebookQualityProfileId || ebookRootFolder?.ebook?.qualityProfileId;
        const ebookMetadataFromRootFolder = ebookRootFolder?.ebookMetadataProfileId || ebookRootFolder?.ebook?.metadataProfileId;
        const audiobookTagsFromRootFolder = audiobookRootFolder?.audiobookTags || audiobookRootFolder?.audiobook?.tags;
        const ebookTagsFromRootFolder = ebookRootFolder?.ebookTags || ebookRootFolder?.ebook?.tags;

        // Inherit profile defaults from the selected root folder when unset.
        if ((!settings.audiobookQualityProfileId || settings.audiobookQualityProfileId.value === 0) && audiobookQualityFromRootFolder) {
          settings.audiobookQualityProfileId = { ...(settings.audiobookQualityProfileId || {}), value: audiobookQualityFromRootFolder };
        }

        if ((!settings.audiobookMetadataProfileId || settings.audiobookMetadataProfileId.value === 0) && audiobookMetadataFromRootFolder) {
          settings.audiobookMetadataProfileId = { ...(settings.audiobookMetadataProfileId || {}), value: audiobookMetadataFromRootFolder };
        }

        if ((!settings.ebookQualityProfileId || settings.ebookQualityProfileId.value === 0) && ebookQualityFromRootFolder) {
          settings.ebookQualityProfileId = { ...(settings.ebookQualityProfileId || {}), value: ebookQualityFromRootFolder };
        }

        if ((!settings.ebookMetadataProfileId || settings.ebookMetadataProfileId.value === 0) && ebookMetadataFromRootFolder) {
          settings.ebookMetadataProfileId = { ...(settings.ebookMetadataProfileId || {}), value: ebookMetadataFromRootFolder };
        }

        // Inherit tag defaults from the selected root folder when unset.
        if (!settings.audiobookTags && audiobookTagsFromRootFolder != null) {
          settings.audiobookTags = { ...(settings.audiobookTags || {}), value: audiobookTagsFromRootFolder };
        }

        if (!settings.ebookTags && ebookTagsFromRootFolder != null) {
          settings.ebookTags = { ...(settings.ebookTags || {}), value: ebookTagsFromRootFolder };
        }
      }

      // Set quality profile defaults to first available instead of "None"
      if (qualityProfiles.isPopulated && qualityProfiles.items.length > 0) {
        const audiobookProfiles = qualityProfiles.items.filter((p) =>
          p.profileType === 1 || p.profileType === 'audiobook'
        );
        const ebookProfiles = qualityProfiles.items.filter((p) =>
          p.profileType === 2 || p.profileType === 'ebook'
        );

        if ((!settings.audiobookQualityProfileId || settings.audiobookQualityProfileId.value === 0) && audiobookProfiles.length > 0) {
          settings.audiobookQualityProfileId = { ...(settings.audiobookQualityProfileId || {}), value: audiobookProfiles[0].id };
        }

        if ((!settings.ebookQualityProfileId || settings.ebookQualityProfileId.value === 0) && ebookProfiles.length > 0) {
          settings.ebookQualityProfileId = { ...(settings.ebookQualityProfileId || {}), value: ebookProfiles[0].id };
        }
      }

      // Set metadata profile defaults to first available instead of "None"
      if (metadataProfiles.isPopulated && metadataProfiles.items.length > 0) {
        const audiobookMetaProfiles = metadataProfiles.items.filter((p) =>
          p.profileType === 1 || p.profileType === 'audiobook'
        );
        const ebookMetaProfiles = metadataProfiles.items.filter((p) =>
          p.profileType === 2 || p.profileType === 'ebook'
        );

        if ((!settings.audiobookMetadataProfileId || settings.audiobookMetadataProfileId.value === 0) && audiobookMetaProfiles.length > 0) {
          settings.audiobookMetadataProfileId = { ...(settings.audiobookMetadataProfileId || {}), value: audiobookMetaProfiles[0].id };
        }

        if ((!settings.ebookMetadataProfileId || settings.ebookMetadataProfileId.value === 0) && ebookMetaProfiles.length > 0) {
          settings.ebookMetadataProfileId = { ...(settings.ebookMetadataProfileId || {}), value: ebookMetaProfiles[0].id };
        }
      }

      const isFetchingSettings = rootFoldersState.isFetching;

      return {
        isSmallScreen: dimensions.isSmallScreen,
        isAdding,
        addError,
        defaultMediaType: uiState.item?.addNewDefaultMediaType,
        isSavingDefaultMediaType: uiState.isSaving,
        uiSaveError: uiState.saveError,
        rootFoldersPopulated: rootFoldersState.isPopulated,
        showMetadataProfile: metadataProfiles.items.length > 2, // NONE and one other
        isWindows: systemStatus.isWindows,
        validationErrors,
        validationWarnings,
        isFetchingSettings,
        ...settings
      };
    }
  );
}

function mapDispatchToProps(dispatch, props) {
  return {
    setAuthorAddDefault: (payload) => dispatch(setAuthorAddDefault(payload)),
    fetchRootFolders: () => dispatch(fetchRootFolders()),
    setUISettingsValue: (payload) => dispatch(setUISettingsValue(payload)),
    saveUISettings: () => dispatch(saveUISettings()),
    dispatchShowMessage: (payload) => dispatch(showMessage(payload))
  };
}

class AddNewSeriesModalContentConnector extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetchingSeries: false,
      seriesDetails: null,
      fetchError: null,
      isAddingSeries: false,
      addError: null
    };

    this._pendingDefaultSave = false;
  }

  componentDidUpdate(prevProps) {
    if (this._pendingDefaultSave && prevProps.isSavingDefaultMediaType && !this.props.isSavingDefaultMediaType) {
      if (!this.props.uiSaveError) {
        this.props.dispatchShowMessage({
          id: `add-new-default-media-type-saved-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaved',
          message: 'Saved default media type',
          type: 'success',
          hideAfter: 5
        });
      } else {
        this.props.dispatchShowMessage({
          id: `add-new-default-media-type-save-failed-${Date.now()}`,
          name: 'AddNewDefaultMediaTypeSaveFailed',
          message: this.props.uiSaveError?.responseJSON?.message || 'Unable to save default media type',
          type: 'error',
          hideAfter: 8
        });
      }

      this._pendingDefaultSave = false;
    }
  }

  componentDidMount() {
    const {
      rootFoldersPopulated,
      isFetchingSettings,
      fetchRootFolders
    } = this.props;

    if (!rootFoldersPopulated && !isFetchingSettings) {
      fetchRootFolders();
    }

    this.fetchSeriesDetails();
  }

  fetchSeriesDetails = () => {
    const { foreignSeriesId, primaryWorkCount } = this.props;

    if (!foreignSeriesId) {
      return;
    }

    const providerPrefix = foreignSeriesId.includes(':') ? foreignSeriesId.split(':')[0]?.toLowerCase() : '';
    const providerMap = {
      hc: 'hardcover',
      hardcover: 'hardcover',
      gr: 'goodreads',
      goodreads: 'goodreads',
      ol: 'openlibrary',
      openlibrary: 'openlibrary',
      gb: 'googlebooks',
      googlebooks: 'googlebooks',
      az: 'az',
      an: 'audible',
      audible: 'audible'
    };
    const provider = providerMap[providerPrefix] || providerPrefix || 'hardcover';

    this.setState({ isFetchingSeries: true, fetchError: null });

    let lookupUrl = `/series/lookup?foreignSeriesId=${encodeURIComponent(foreignSeriesId)}&provider=${encodeURIComponent(provider)}`;

    if (Number.isFinite(primaryWorkCount) && primaryWorkCount > 0) {
      lookupUrl += `&primaryWorkCount=${encodeURIComponent(primaryWorkCount)}`;
    }

    const ajaxOptions = {
      url: lookupUrl,
      method: 'GET'
    };

    const { request } = createAjaxRequest(ajaxOptions);

    request.done((data) => {
      this.setState({
        isFetchingSeries: false,
        seriesDetails: data,
        fetchError: null
      });
    });

    request.fail((xhr) => {
      const error = xhr.responseJSON?.error || xhr.responseJSON?.message || 'Failed to load series details';
      this.setState({
        isFetchingSeries: false,
        seriesDetails: null,
        fetchError: error
      });
    });
  };

  onInputChange = ({ name, value }) => {
    this.props.setAuthorAddDefault({ [name]: value });
  };

  onSetDefaultMediaType = (mediaType) => {
    this._pendingDefaultSave = true;
    this.props.setUISettingsValue({ name: 'addNewDefaultMediaType', value: mediaType });
    this.props.saveUISettings();
  };

  getApiErrorMessage = (xhr) => {
    if (!xhr) {
      return 'Failed to add series';
    }

    const responseJson = xhr.responseJSON;
    if (responseJson) {
      if (typeof responseJson === 'string') {
        return responseJson;
      }

      const errorMessage = responseJson.errorMessage || responseJson.error || responseJson.message;
      if (errorMessage) {
        return errorMessage;
      }

      // ASP.NET validation problem details
      if (responseJson.title && responseJson.errors) {
        const firstKey = Object.keys(responseJson.errors)[0];
        const firstError = firstKey && Array.isArray(responseJson.errors[firstKey]) ? responseJson.errors[firstKey][0] : null;
        return firstError ? `${responseJson.title}: ${firstError}` : responseJson.title;
      }

      if (responseJson.title) {
        return responseJson.title;
      }
    }

    if (xhr.responseText) {
      return xhr.responseText;
    }

    return 'Failed to add series';
  };

  onAddSeriesPress = ({
    selectedMediaType,
    selectedBooks
  }) => {
    this.setState({ isAddingSeries: true, addError: null });

    const {
      foreignSeriesId,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      audiobookMetadataProfileId,
      ebookMetadataProfileId,
      monitor,
      audiobookMonitored,
      ebookMonitored,
      audiobookMonitor,
      ebookMonitor,
      monitorNewItems,
      audiobookMonitorNewItems,
      ebookMonitorNewItems,
      tags,
      audiobookTags,
      ebookTags
    } = this.props;

    const toPositiveInt = (value) => {
      if (value == null || value === '') {
        return null;
      }

      const parsed = typeof value === 'number' ? value : parseInt(value, 10);
      if (!Number.isFinite(parsed) || parsed <= 0) {
        return null;
      }

      return parsed;
    };

    const buildPayloadForMediaType = (mediaType) => {
      const isAudiobook = mediaType === 'audiobook';
      const monitorExistingUi = isAudiobook ? (audiobookMonitor?.value || monitor?.value) : (ebookMonitor?.value || monitor?.value);
      const normalizedMonitorExistingUi = (monitorExistingUi ?? '').toString().trim().toLowerCase();
      const monitorExisting = normalizedMonitorExistingUi === 'specificbook' ?
        'specificbook' : normalizeMonitorOption(normalizedMonitorExistingUi);

      const monitorNewItemsUi = normalizeMonitorNewItemsOption(isAudiobook ?
        (audiobookMonitorNewItems?.value || monitorNewItems?.value) :
        (ebookMonitorNewItems?.value || monitorNewItems?.value));

      const selectedTags = isAudiobook ?
        (audiobookTags?.value || tags?.value) :
        (ebookTags?.value || tags?.value);

      const payload = {
        foreignSeriesId,
        selectedMediaType: mediaType,
        selectedBooks,
        monitor: monitorExisting,
        tags: selectedTags
      };

      if (isAudiobook) {
        payload.audiobookMonitored = audiobookMonitored?.value !== false;
        payload.audiobookMonitorExistingMode = monitorExisting;
        payload.audiobookMonitorNewItems = monitorNewItemsUi;
      } else {
        payload.ebookMonitored = ebookMonitored?.value !== false;
        payload.ebookMonitorExistingMode = monitorExisting;
        payload.ebookMonitorNewItems = monitorNewItemsUi;
      }

      if (mediaType === 'audiobook') {
        const rootFolderPath = audiobookRootFolderPath?.value;
        if (rootFolderPath && String(rootFolderPath).trim()) {
          payload.audiobookRootFolderPath = String(rootFolderPath).trim();
        }

        const qualityId = toPositiveInt(audiobookQualityProfileId?.value);
        if (qualityId != null) {
          payload.audiobookQualityProfileId = qualityId;
        }

        const metadataId = toPositiveInt(audiobookMetadataProfileId?.value);
        if (metadataId != null) {
          payload.audiobookMetadataProfileId = metadataId;
        }
      } else if (mediaType === 'ebook') {
        const rootFolderPath = ebookRootFolderPath?.value;
        if (rootFolderPath && String(rootFolderPath).trim()) {
          payload.ebookRootFolderPath = String(rootFolderPath).trim();
        }

        const qualityId = toPositiveInt(ebookQualityProfileId?.value);
        if (qualityId != null) {
          payload.ebookQualityProfileId = qualityId;
        }

        const metadataId = toPositiveInt(ebookMetadataProfileId?.value);
        if (metadataId != null) {
          payload.ebookMetadataProfileId = metadataId;
        }
      }

      return payload;
    };

    const postAddSeries = (payload) => {
      const ajaxOptions = {
        url: '/series/add',
        method: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        data: JSON.stringify(payload)
      };

      return createAjaxRequest(ajaxOptions).request;
    };

    const handleSuccess = (data, mediaTypeLabel) => {
      const authorsCount = data.addedAuthors?.length || 0;
      const booksCount = data.monitoredBooks?.length || 0;
      const pendingCount = data.pendingAuthorImportIds?.length || 0;
      const labelSuffix = mediaTypeLabel ? ` (${mediaTypeLabel})` : '';
      const queueSuffix = pendingCount > 0 ? `, queued ${pendingCount} author(s) for import` : '';
      const message = `Added series "${this.props.title}"${labelSuffix} — ${authorsCount} author(s) added${queueSuffix}, monitoring ${booksCount} book(s)`;

      this.setState({ isAddingSeries: false, addError: null });

      this.props.dispatchShowMessage({
        id: `series-added-${Date.now()}`,
        name: 'SeriesAdded',
        message,
        type: 'success',
        hideAfter: 8
      });

      this.props.onModalClose();

      // Refresh the UI so newly added authors/books appear immediately.
      if (authorsCount > 0) {
        window.location.href = '/';
      }
    };

    const handleFailure = (errorMessage) => {
      this.setState({ isAddingSeries: false, addError: errorMessage });

      this.props.dispatchShowMessage({
        id: `series-add-error-${Date.now()}`,
        name: 'SeriesAddError',
        message: errorMessage,
        type: 'error',
        hideAfter: 12
      });
    };

    const validateResultOrThrow = (data) => {
      if (!data || !data.success) {
        const error = data?.errorMessage || 'Failed to add series';
        throw new Error(error);
      }

      return data;
    };

    // Support "Both" by submitting two sequential adds (audiobook then ebook).
    // Backend expects a single mediaType per request.
    if (selectedMediaType === 'both') {
      const firstPayload = buildPayloadForMediaType('audiobook');
      const secondPayload = buildPayloadForMediaType('ebook');

      const first = postAddSeries(firstPayload);

      first.done((data1) => {
        try {
          validateResultOrThrow(data1);
        } catch (e) {
          handleFailure(e.message || 'Failed to add series');
          return;
        }

        const second = postAddSeries(secondPayload);

        second.done((data2) => {
          try {
            validateResultOrThrow(data2);
          } catch (e) {
            const partialError = e.message || 'Failed to add series';
            const authorsCount = data1?.addedAuthors?.length || 0;
            const booksCount = data1?.monitoredBooks?.length || 0;
            const pendingCount = data1?.pendingAuthorImportIds?.length || 0;
            const queueSuffix = pendingCount > 0 ? `, queued ${pendingCount} author(s) for import` : '';
            const message = `Added series "${this.props.title}" (Audiobooks) — ${authorsCount} author(s) added${queueSuffix}, monitoring ${booksCount} book(s). Failed to add eBooks: ${partialError}`;
            handleFailure(message);
            return;
          }

          const audiobookAuthors = data1?.addedAuthors?.length || 0;
          const audiobookBooks = data1?.monitoredBooks?.length || 0;
          const ebookAuthors = data2?.addedAuthors?.length || 0;
          const ebookBooks = data2?.monitoredBooks?.length || 0;
          const pendingIds = new Set([
            ...(data1?.pendingAuthorImportIds || []),
            ...(data2?.pendingAuthorImportIds || [])
          ]);
          const pendingCount = pendingIds.size;
          const queueSuffix = pendingCount > 0 ? `; queued ${pendingCount} author(s) for import` : '';
          const message = `Added series "${this.props.title}" — Audiobooks: ${audiobookAuthors} author(s), ${audiobookBooks} book(s); eBooks: ${ebookAuthors} author(s), ${ebookBooks} book(s)${queueSuffix}`;

          this.setState({ isAddingSeries: false, addError: null });

          this.props.dispatchShowMessage({
            id: `series-added-both-${Date.now()}`,
            name: 'SeriesAdded',
            message,
            type: 'success',
            hideAfter: 10
          });

          this.props.onModalClose();

          if (audiobookAuthors + ebookAuthors > 0) {
            window.location.href = '/';
          }
        });

        second.fail((xhr2) => {
          const partialError = this.getApiErrorMessage(xhr2);
          const authorsCount = data1?.addedAuthors?.length || 0;
          const booksCount = data1?.monitoredBooks?.length || 0;
          const pendingCount = data1?.pendingAuthorImportIds?.length || 0;
          const queueSuffix = pendingCount > 0 ? `, queued ${pendingCount} author(s) for import` : '';
          const message = `Added series "${this.props.title}" (Audiobooks) — ${authorsCount} author(s) added${queueSuffix}, monitoring ${booksCount} book(s). Failed to add eBooks: ${partialError}`;
          handleFailure(message);
        });
      });

      first.fail((xhr1) => {
        const error = this.getApiErrorMessage(xhr1);
        handleFailure(error);
      });

      return;
    }

    const request = postAddSeries(buildPayloadForMediaType(selectedMediaType));

    request.done((data) => {
      try {
        validateResultOrThrow(data);
      } catch (e) {
        handleFailure(e.message || 'Failed to add series');
        return;
      }

      const label = selectedMediaType === 'audiobook' ? 'Audiobooks' : (selectedMediaType === 'ebook' ? 'eBooks' : null);
      handleSuccess(data, label);
    });

    request.fail((xhr) => {
      const error = this.getApiErrorMessage(xhr);
      handleFailure(error);
    });
  };

  render() {
    const { isFetchingSettings } = this.props;
    const { isFetchingSeries, seriesDetails, fetchError } = this.state;

    return (
      <AddNewSeriesModalContent
        {...this.props}
        seriesDetails={seriesDetails}
        isFetching={isFetchingSeries || isFetchingSettings}
        fetchError={fetchError}
        isAddingSeries={this.state.isAddingSeries}
        addError={this.state.addError}
        onSetDefaultMediaType={this.onSetDefaultMediaType}
        onInputChange={this.onInputChange}
        onAddSeriesPress={this.onAddSeriesPress}
      />
    );
  }
}

const connectedComponent = connect(createMapStateToProps, mapDispatchToProps)(AddNewSeriesModalContentConnector);

export default connectedComponent;
