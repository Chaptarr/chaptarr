import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { saveAuthor } from 'Store/Actions/authorActions';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import SelectAuthorRootFolderModalContent from './SelectAuthorRootFolderModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors,
    (authors) => {
      const {
        isSaving,
        saveError
      } = authors;

      return {
        isSaving,
        saveError
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchSaveAuthor: saveAuthor
};

class SelectAuthorRootFolderModalContentConnector extends Component {

  componentDidMount() {
    this.props.dispatchFetchRootFolders();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose(true);
    }
  }

  onSavePress = (rootFolderPath) => {
    const { authorId, mediaType } = this.props;

    const payload = {
      id: authorId,
      lastSelectedMediaType: mediaType
    };

    if (mediaType === 'ebook') {
      payload.ebookRootFolderPath = rootFolderPath;
    } else {
      payload.audiobookRootFolderPath = rootFolderPath;
    }

    this.props.dispatchSaveAuthor(payload);
  };

  render() {
    const {
      dispatchFetchRootFolders,
      dispatchSaveAuthor,
      ...otherProps
    } = this.props;

    return (
      <SelectAuthorRootFolderModalContent
        {...otherProps}
        onSavePress={this.onSavePress}
      />
    );
  }
}

SelectAuthorRootFolderModalContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  onModalClose: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchSaveAuthor: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(SelectAuthorRootFolderModalContentConnector);

