import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import {
  deleteNotification,
  fetchRootFolders,
  saveNotification,
  setNotificationFieldValue,
  setNotificationValue,
  testNotification,
  toggleAdvancedSettings
} from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import EditNotificationModalContent from './EditNotificationModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createProviderSettingsSelector('notifications'),
    (state) => state.settings.rootFolders,
    (advancedSettings, notification, rootFolders) => {
      return {
        advancedSettings,
        ...notification,
        rootFolders: rootFolders.items || [],
        isRootFoldersPopulated: rootFolders.isPopulated,
        isRootFoldersFetching: rootFolders.isFetching,
        rootFoldersError: rootFolders.error
      };
    }
  );
}

const mapDispatchToProps = {
  deleteNotification,
  fetchRootFolders,
  setNotificationValue,
  setNotificationFieldValue,
  saveNotification,
  testNotification,
  toggleAdvancedSettings
};

class EditNotificationModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.fetchRootFoldersIfNeeded();
  }

  componentDidUpdate(prevProps, prevState) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }

    if (prevProps.isDeleting && !this.props.isDeleting && !this.props.deleteError) {
      this.props.onModalClose();
    }

    if (prevProps.item.implementationName !== this.props.item.implementationName ||
        prevProps.isRootFoldersPopulated !== this.props.isRootFoldersPopulated) {
      this.fetchRootFoldersIfNeeded();
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setNotificationValue({ name, value });
  };

  onFieldChange = ({ name, value }) => {
    this.props.setNotificationFieldValue({ name, value });
  };

  onSavePress = () => {
    const { item, id } = this.props;

    // Set default name for AudioBookShelf if name is empty
    if (item.implementationName === 'AudioBookShelf' && (!item.name || !item.name.value || item.name.value.trim() === '')) {
      this.props.setNotificationValue({ name: 'name', value: 'ABS' });
      this.props.saveNotification({ id, name: 'ABS' });
    } else {
      this.props.saveNotification({ id });
    }
  };

  onTestPress = () => {
    const { item, id } = this.props;

    // Set default name for AudioBookShelf if name is empty
    if (item.implementationName === 'AudioBookShelf' && (!item.name || !item.name.value || item.name.value.trim() === '')) {
      this.props.setNotificationValue({ name: 'name', value: 'ABS' });
      this.props.testNotification({ id, name: 'ABS' });
    } else {
      this.props.testNotification({ id });
    }
  };

  onAdvancedSettingsPress = () => {
    this.props.toggleAdvancedSettings();
  };

  onDeleteNotificationPress = () => {
    if (this.props.onDeleteNotificationPress) {
      this.props.onDeleteNotificationPress();
      return;
    }

    this.props.deleteNotification({ id: this.props.id });
  };

  fetchRootFoldersIfNeeded = () => {
    if (this.props.item.implementationName === 'AudioBookShelf' && !this.props.isRootFoldersPopulated) {
      this.props.fetchRootFolders();
    }
  };

  //
  // Render

  render() {
    return (
      <EditNotificationModalContent
        {...this.props}
        onSavePress={this.onSavePress}
        onTestPress={this.onTestPress}
        onAdvancedSettingsPress={this.onAdvancedSettingsPress}
        onInputChange={this.onInputChange}
        onFieldChange={this.onFieldChange}
        onDeleteNotificationPress={this.onDeleteNotificationPress}
      />
    );
  }
}

EditNotificationModalContentConnector.propTypes = {
  id: PropTypes.number,
  isFetching: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  item: PropTypes.object.isRequired,
  deleteNotification: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  onDeleteNotificationPress: PropTypes.func,
  setNotificationValue: PropTypes.func.isRequired,
  setNotificationFieldValue: PropTypes.func.isRequired,
  saveNotification: PropTypes.func.isRequired,
  testNotification: PropTypes.func.isRequired,
  toggleAdvancedSettings: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  rootFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  isRootFoldersPopulated: PropTypes.bool.isRequired,
  isRootFoldersFetching: PropTypes.bool,
  rootFoldersError: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditNotificationModalContentConnector);
