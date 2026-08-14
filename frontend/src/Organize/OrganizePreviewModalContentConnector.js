import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchOrganizePreview } from 'Store/Actions/organizePreviewActions';
import { fetchNamingSettings } from 'Store/Actions/settingsActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import OrganizePreviewModalContent from './OrganizePreviewModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.organizePreview,
    (state) => state.settings.naming,
    createAuthorSelector(),
    (state, props) => props.mediaType,
    (organizePreview, naming, author, mediaType) => {
      const props = { ...organizePreview };
      props.isFetching = organizePreview.isFetching || naming.isFetching;
      props.isPopulated = organizePreview.isPopulated && naming.isPopulated;
      props.error = organizePreview.error || naming.error;
      const effectiveMediaType = (mediaType || '').toLowerCase();
      props.renameBooksEnabled = effectiveMediaType === 'ebook' ?
        !!naming.item.ebookRenameBooks :
        !!naming.item.renameBooks;
      props.trackFormat = effectiveMediaType === 'ebook' ?
        (naming.item.ebookStandardBookFormat || naming.item.standardBookFormat) :
        naming.item.standardBookFormat;
      props.path = effectiveMediaType === 'ebook' ?
        (author.ebookPath || author.path) :
        (author.audiobookPath || author.path);

      return props;
    }
  );
}

const mapDispatchToProps = {
  fetchOrganizePreview,
  fetchNamingSettings,
  executeCommand
};

class OrganizePreviewModalContentConnector extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      moveToCanonicalAuthorFolder: false
    };
  }

  //
  // Lifecycle

  componentDidMount() {
    const {
      authorId,
      bookId,
      mediaType
    } = this.props;

    this.props.fetchOrganizePreview({
      authorId,
      bookId,
      mediaType,
      moveToCanonicalAuthorFolder: false
    });

    this.props.fetchNamingSettings();
  }

  //
  // Listeners

  onOrganizePress = (files) => {
    this.props.executeCommand({
      name: commandNames.RENAME_FILES,
      authorId: this.props.authorId,
      moveToCanonicalAuthorFolder: this.state.moveToCanonicalAuthorFolder,
      files
    });

    this.props.onModalClose();
  };

  onMoveToCanonicalAuthorFolderChange = ({ value }) => {
    this.setState({ moveToCanonicalAuthorFolder: value });
    this.props.fetchOrganizePreview({
      authorId: this.props.authorId,
      bookId: this.props.bookId,
      mediaType: this.props.mediaType,
      moveToCanonicalAuthorFolder: value
    });
  };

  //
  // Render

  render() {
    // Book-level previews stay track-in-place; canonical consolidation is an explicit author-level action.
    const canMoveToCanonicalAuthorFolder = !this.props.bookId;

    return (
      <OrganizePreviewModalContent
        {...this.props}
        canMoveToCanonicalAuthorFolder={canMoveToCanonicalAuthorFolder}
        moveToCanonicalAuthorFolder={this.state.moveToCanonicalAuthorFolder}
        onMoveToCanonicalAuthorFolderChange={this.onMoveToCanonicalAuthorFolderChange}
        onOrganizePress={this.onOrganizePress}
      />
    );
  }
}

OrganizePreviewModalContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  bookId: PropTypes.number,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  fetchOrganizePreview: PropTypes.func.isRequired,
  fetchNamingSettings: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(OrganizePreviewModalContentConnector);
