import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import {
  saveImportList,
  setImportListFieldValue,
  setImportListValue,
  testImportList,
  toggleAdvancedSettings
} from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import EditImportListModalContent from './EditImportListModalContent';

function getMonitoringDefaults(root, mediaType) {
  return {
    [`${mediaType}Monitored`]: root[`${mediaType}Monitored`] ?? root[mediaType]?.monitored,
    [`${mediaType}Monitor`]: root[`${mediaType}MonitorExistingMode`] ?? root[mediaType]?.monitorExistingMode,
    [`${mediaType}MonitorNewItems`]: root[`${mediaType}MonitorNewItems`] ?? root[mediaType]?.monitorNewItems
  };
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createProviderSettingsSelector('importLists'),
    (state) => state.settings.rootFolders,
    (advancedSettings, importList, rootFolders) => {
      const monitoringDefaults = {};
      ['audiobook', 'ebook'].forEach((mediaType) => {
        const rootPath = importList.item.fields?.find((field) => field.name === `${mediaType}RootFolderPath`)?.value;
        const root = rootFolders.items.find((folder) => folder.path === rootPath);
        if (getRootFolderMediaTypes(root).includes(mediaType)) {
          Object.assign(monitoringDefaults, getMonitoringDefaults(root, mediaType));
        }
      });

      return {
        advancedSettings,
        rootFolders: rootFolders.items,
        isRootFoldersPopulated: rootFolders.isPopulated,
        rootFoldersError: rootFolders.error,
        monitoringDefaults,
        ...importList
      };
    }
  );
}

const mapDispatchToProps = {
  fetchRootFolders,
  setImportListValue,
  setImportListFieldValue,
  saveImportList,
  testImportList,
  toggleAdvancedSettings
};

class EditImportListModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchRootFolders();
    if (!this.props.id) {
      this.fillRootDefaults(['audiobookRootFolderPath', 'ebookRootFolderPath']);
    }
  }

  componentDidUpdate(prevProps) {
    const initialRootsLoaded = !this.props.id && !prevProps.rootFolders.length && this.props.rootFolders.length;
    const changedRoots = ['audiobookRootFolderPath', 'ebookRootFolderPath'].filter((name) => {
      return initialRootsLoaded ||
        this.props.item.fields?.find((field) => field.name === name)?.value !==
        prevProps.item.fields?.find((field) => field.name === name)?.value;
    });
    if (changedRoots.length) {
      this.fillRootDefaults(changedRoots);
    }

    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }
  }

  fillRootDefaults = (changedRoots) => {
    const { item, rootFolders } = this.props;
    const before = item.monitoringOptions?.value || {};
    const options = { ...before };
    changedRoots.forEach((name) => {
      const mediaType = name === 'audiobookRootFolderPath' ? 'audiobook' : 'ebook';
      const rootPath = item.fields?.find((field) => field.name === name)?.value;
      const root = rootFolders.find((folder) => folder.path === rootPath);
      if (!getRootFolderMediaTypes(root).includes(mediaType)) {
        return;
      }

      Object.assign(options, getMonitoringDefaults(root, mediaType));
      ['QualityProfileId', 'MetadataProfileId', 'Tags'].forEach((suffix) => {
        const fieldName = `${mediaType}${suffix}`;
        const field = item.fields.find((value) => value.name === fieldName);
        const value = root[fieldName] ?? root[mediaType]?.[suffix[0].toLowerCase() + suffix.slice(1)] ?? (suffix === 'Tags' ? [] : 0);
        if (field && JSON.stringify(field.value) !== JSON.stringify(value)) {
          this.props.setImportListFieldValue({ name: fieldName, value });
        }
      });
    });
    if (JSON.stringify(before) !== JSON.stringify(options)) {
      this.props.setImportListValue({ name: 'monitoringOptions', value: options });
    }
  };

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setImportListValue({ name, value });
  };

  onFieldChange = ({ name, value }) => {
    this.props.setImportListFieldValue({ name, value });
  };

  onSavePress = () => {
    if (this.props.item.monitoringOptions?.value?.addMissingAuthors !== false && !this.props.isRootFoldersPopulated) {
      return;
    }
    this.props.saveImportList({ id: this.props.id });
  };

  onTestPress = () => {
    this.props.testImportList({ id: this.props.id });
  };

  onAdvancedSettingsPress = () => {
    this.props.toggleAdvancedSettings();
  };

  //
  // Render

  render() {
    return (
      <EditImportListModalContent
        {...this.props}
        onSavePress={this.onSavePress}
        onTestPress={this.onTestPress}
        onAdvancedSettingsPress={this.onAdvancedSettingsPress}
        onInputChange={this.onInputChange}
        onFieldChange={this.onFieldChange}
      />
    );
  }
}

EditImportListModalContentConnector.propTypes = {
  id: PropTypes.number,
  rootFolders: PropTypes.array.isRequired,
  isRootFoldersPopulated: PropTypes.bool.isRequired,
  rootFoldersError: PropTypes.object,
  fetchRootFolders: PropTypes.func.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  setImportListValue: PropTypes.func.isRequired,
  setImportListFieldValue: PropTypes.func.isRequired,
  saveImportList: PropTypes.func.isRequired,
  testImportList: PropTypes.func.isRequired,
  toggleAdvancedSettings: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditImportListModalContentConnector);
