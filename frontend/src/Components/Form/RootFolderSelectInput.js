import PropTypes from 'prop-types';
import React, { Component } from 'react';
import EditRootFolderModalConnector from 'Settings/MediaManagement/RootFolder/EditRootFolderModalConnector';
import EnhancedSelectInput from './EnhancedSelectInput';
import RootFolderSelectInputOption from './RootFolderSelectInputOption';
import RootFolderSelectInputSelectedValue from './RootFolderSelectInputSelectedValue';

class RootFolderSelectInput extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddNewRootFolderModalOpen: false
    };
  }

  componentDidUpdate(prevProps) {
    const {
      name,
      isSaving,
      saveError,
      onChange
    } = this.props;

    const newRootFolderPath = this.state.newRootFolderPath;

    if (
      prevProps.isSaving &&
      !isSaving &&
      !saveError &&
      newRootFolderPath
    ) {
      onChange({ name, value: newRootFolderPath });
      this.setState({ newRootFolderPath: '' });
    }
  }

  //
  // Listeners

  onChange = ({ name, value }) => {
    if (value === 'addNew') {
      this.setState({ isAddNewRootFolderModalOpen: true });
    } else {
      this.props.onChange({ name, value });
    }
  };

  onNewRootFolderSelect = ({ value }) => {
    const { name, onChange } = this.props;
    onChange({ name, value });
    this.setState({ newRootFolderPath: '' });
  };

  onAddRootFolderModalClose = () => {
    this.setState({ isAddNewRootFolderModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      includeNoChange,
      ...otherProps
    } = this.props;

    return (
      <div>
        <EnhancedSelectInput
          {...otherProps}
          selectedValueComponent={RootFolderSelectInputSelectedValue}
          optionComponent={RootFolderSelectInputOption}
          onChange={this.onChange}
        />

        <EditRootFolderModalConnector
          isOpen={this.state.isAddNewRootFolderModalOpen}
          folderType={this.props.folderType}
          onModalClose={this.onAddRootFolderModalClose}
          onRootFolderAdded={this.onNewRootFolderSelect}
        />
      </div>
    );
  }
}

RootFolderSelectInput.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.string,
  values: PropTypes.arrayOf(PropTypes.object).isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  includeNoChange: PropTypes.bool.isRequired,
  folderType: PropTypes.number,
  onChange: PropTypes.func.isRequired
};

RootFolderSelectInput.defaultProps = {
  includeNoChange: false
};

export default RootFolderSelectInput;
