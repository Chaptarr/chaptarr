import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { hideAuthorFolderPicker } from 'Store/Actions/authorFolderActions';
import Modal from 'Components/Modal/Modal';
import AuthorFolderPickerModalContent from 'Components/Modal/AuthorFolderPicker/AuthorFolderPickerModalConnector';

function createMapStateToProps() {
  return createSelector(
    (state) => state.authorFolder,
    (authorFolder) => {
      const {
        isModalOpen,
        authorId,
        authorName,
        rootFolderId,
        matches
      } = authorFolder;

      return {
        isOpen: isModalOpen,
        authorId,
        authorName,
        rootFolderId,
        matches
      };
    }
  );
}

const mapDispatchToProps = {
  hideAuthorFolderPicker
};

class AuthorFolderPickerModalConnector extends Component {

  //
  // Listeners

  onModalClose = () => {
    this.props.hideAuthorFolderPicker();
  };

  //
  // Render

  render() {
    const {
      isOpen,
      authorId,
      authorName,
      rootFolderId,
      matches
    } = this.props;

    return (
      <Modal
        isOpen={isOpen}
        onModalClose={this.onModalClose}
      >
        <AuthorFolderPickerModalContent
          authorId={authorId}
          authorName={authorName}
          rootFolderId={rootFolderId}
          matches={matches}
          onModalClose={this.onModalClose}
        />
      </Modal>
    );
  }
}

AuthorFolderPickerModalConnector.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  authorId: PropTypes.number,
  authorName: PropTypes.string,
  rootFolderId: PropTypes.number,
  matches: PropTypes.array,
  hideAuthorFolderPicker: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorFolderPickerModalConnector);