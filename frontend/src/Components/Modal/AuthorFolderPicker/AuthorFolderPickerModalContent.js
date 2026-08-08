import PropTypes from 'prop-types';
import React, { Component } from 'react';
import AuthorFolderPicker from './AuthorFolderPicker';

class AuthorFolderPickerModalContent extends Component {

  //
  // Listeners

  onLinkAuthorPress = (folderPath) => {
    const {
      authorId,
      rootFolderId,
      linkAuthorToFolder,
      onModalClose
    } = this.props;

    linkAuthorToFolder({
      authorId,
      rootFolderId,
      folderPath
    });

    onModalClose();
  }

  //
  // Render

  render() {
    return (
      <AuthorFolderPicker
        {...this.props}
        onLinkAuthorPress={this.onLinkAuthorPress}
      />
    );
  }
}

AuthorFolderPickerModalContent.propTypes = {
  authorId: PropTypes.number.isRequired,
  rootFolderId: PropTypes.number.isRequired,
  authorName: PropTypes.string.isRequired,
  matches: PropTypes.arrayOf(PropTypes.object).isRequired,
  isLinking: PropTypes.bool.isRequired,
  linkAuthorToFolder: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default AuthorFolderPickerModalContent;