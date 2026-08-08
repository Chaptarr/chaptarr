import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { FolderType, coerceFolderType } from 'Helpers/Props/folderTypes';
import translate from 'Utilities/String/translate';
import RootFolderSelectInput from './RootFolderSelectInput';

const ADD_NEW_KEY = 'addNew';

function unwrapValue(rawValue) {
  let value = rawValue;

  // Some callers (or persisted state) may provide values in a wrapped `{ value }` shape.
  // EnhancedSelectInput expects the raw key (string path).
  // Use a small hard cap + self-reference check to avoid accidental infinite loops.
  for (let i = 0; i < 10; i++) {
    if (!value || typeof value !== 'object' || !Object.prototype.hasOwnProperty.call(value, 'value')) {
      break;
    }

    const nextValue = value.value;
    if (nextValue === value) {
      break;
    }

    value = nextValue;
  }

  return value;
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.rootFolders,
    (state, { value }) => unwrapValue(value),
    (state, { includeMissingValue }) => includeMissingValue,
    (state, { includeNoChange }) => includeNoChange,
    (state, { includeNoChangeDisabled }) => includeNoChangeDisabled,
    (state, { folderType }) => folderType,
    (state, { includeMixed = true }) => includeMixed,
    (rootFolders, value, includeMissingValue, includeNoChange, includeNoChangeDisabled = true, folderType, includeMixed) => {
      const normalizedFolderType = coerceFolderType(folderType);

      // Filter root folders by folderType if specified (audiobook/ebook only)
      // Include folders that match the type, and optionally mixed folders.
      let filteredFolders = rootFolders.items;
      if (normalizedFolderType === FolderType.Audiobook || normalizedFolderType === FolderType.Ebook) {
        filteredFolders = rootFolders.items.filter((folder) => {
          const folderFolderType = coerceFolderType(folder.folderType);
          if (folderFolderType === normalizedFolderType) {
            return true;
          }

          return includeMixed && folderFolderType === FolderType.Mixed;
        });
      }

      const values = filteredFolders.map((rootFolder) => {
        return {
          key: rootFolder.path,
          value: rootFolder.path,
          name: rootFolder.name,
          freeSpace: rootFolder.freeSpace,
          isMissing: false
        };
      });

      if (includeNoChange) {
        values.unshift({
          key: 'noChange',
          value: translate('NoChange'),
          isDisabled: includeNoChangeDisabled,
          isMissing: false
        });
      }

      if (!values.length) {
        values.push({
          key: '',
          value: '',
          isDisabled: true,
          isHidden: true
        });
      }

      if (includeMissingValue && value != null && value !== '' && !values.find((v) => v.key === value)) {
        values.push({
          key: value,
          value,
          isMissing: true,
          isDisabled: true
        });
      }

      values.push({
        key: ADD_NEW_KEY,
        value: 'Add a new path'
      });

      return {
        value,
        values,
        isSaving: rootFolders.isSaving,
        saveError: rootFolders.saveError,
        folderType: normalizedFolderType  // Pass folderType through to child components
      };
    }
  );
}

class RootFolderSelectInputConnector extends Component {

  //
  // Lifecycle

  constructor(props) {
    super(props);
    
    const {
      name,
      value,
      values,
      onChange
    } = props;

    if (value == null && values[0].key === '') {
      onChange({ name, value: '' });
    }
  }

  componentDidMount() {
    const {
      name,
      value,
      values,
      onChange,
      folderType
    } = this.props;

    // If folderType is specified and no value is set, default to first folder of that type
    if (folderType && !value) {
      const defaultValue = values.find((v) => v.key && v.key !== ADD_NEW_KEY && v.key !== 'noChange');
      if (defaultValue) {
        onChange({ name, value: defaultValue.key });
        return;
      }
    }

    // If the current value isn't present in the options, do not silently replace it with a different folder.
    // Clear it so the user must make an explicit selection (or enable includeMissingValue in the caller).
    if (value && value !== ADD_NEW_KEY && !values.some((v) => v.key === value)) {
      onChange({ name, value: '' });
      return;
    }

    if (!value || value === ADD_NEW_KEY) {
      const defaultValue = values[0];

      if (defaultValue.key === ADD_NEW_KEY) {
        onChange({ name, value: '' });
      } else {
        onChange({ name, value: defaultValue.key });
      }
    }
  }

  componentDidUpdate(prevProps) {
    const {
      name,
      value,
      values,
      onChange,
      folderType
    } = this.props;

    if (prevProps.values === values && prevProps.folderType === folderType) {
      return;
    }

    // If folderType is specified and no valid value is set, default to first folder of that type
    if (value && value !== ADD_NEW_KEY && !values.some((v) => v.key === value)) {
      onChange({ name, value: '' });
      return;
    }

    if (folderType && !value) {
      const defaultValue = values.find((v) => v.key && v.key !== ADD_NEW_KEY && v.key !== 'noChange');
      if (defaultValue) {
        onChange({ name, value: defaultValue.key });
        return;
      }
    }

    if (!value && values.length && values.some((v) => !!v.key && v.key !== ADD_NEW_KEY)) {
      const defaultValue = values[0];

      if (defaultValue.key !== ADD_NEW_KEY) {
        onChange({ name, value: defaultValue.key });
      }
    }
  }

  //
  // Render

  render() {
    const {
      ...otherProps
    } = this.props;

    return (
      <RootFolderSelectInput
        {...otherProps}
        onNewRootFolderSelect={this.onNewRootFolderSelect}
      />
    );
  }
}

RootFolderSelectInputConnector.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.string,
  values: PropTypes.arrayOf(PropTypes.object).isRequired,
  includeNoChange: PropTypes.bool.isRequired,
  includeMixed: PropTypes.bool,
  folderType: PropTypes.number,
  onChange: PropTypes.func.isRequired
};

RootFolderSelectInputConnector.defaultProps = {
  includeNoChange: false,
  includeMixed: true
};

export default connect(createMapStateToProps)(RootFolderSelectInputConnector);
