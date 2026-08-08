/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { deleteBookFile, deleteBookFiles, setBookFilesSort, updateBookFiles } from 'Store/Actions/bookFileActions';
import { fetchQualityProfileSchema } from 'Store/Actions/settingsActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import getQualities from 'Utilities/Quality/getQualities';
import BookFileEditorTableContent from './BookFileEditorTableContent';

function createSchemaSelector() {
  return createSelector(
    (state) => state.settings.qualityProfiles,
    (qualityProfiles) => {
      const qualities = getQualities(qualityProfiles.schema.items);

      let error = null;

      if (qualityProfiles.schemaError) {
        error = 'Unable to load qualities';
      }

      return {
        isFetching: qualityProfiles.isSchemaFetching,
        isPopulated: qualityProfiles.isSchemaPopulated,
        error,
        qualities
      };
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    (state, { authorId }) => authorId,
    (state, { bookId }) => bookId,
    (state, { selectedMediaType }) => selectedMediaType,
    (state, { filterValue }) => filterValue,
    createClientSideCollectionSelector('bookFiles'),
    createSchemaSelector(),
    createAuthorSelector(),
    (
      authorId,
      bookId,
      selectedMediaType,
      filterValue,
      bookFiles,
      schema,
      author
    ) => {
      let {
        items,
        ...otherProps
      } = bookFiles;
      
      // CRITICAL FIX: Filter by authorId to prevent showing files from other authors during imports
      // This fixes the bug where SignalR broadcasts add all files to the global store
      // Also filter by bookId when rendering a book-scoped view to avoid cross-book leakage.
      if (bookId) {
        items = items.filter((file) => file.bookId === bookId);
      }

      if (authorId) {
        items = items.filter(file => {
          // Check if the file has an authorId directly
          if (file.authorId === authorId) {
            return true;
          }
          // Check if the file's book has the matching authorId
          if (file.book && file.book.authorId === authorId) {
            return true;
          }
          // Check if the file's author matches (for backwards compatibility)
          if (file.author && file.author.id === authorId) {
            return true;
          }
          return false;
        });
      }
      
      // Apply media type filter
      if (selectedMediaType) {
        // Both selectedMediaType and bookFile.mediaType use lowercase
        // selectedMediaType is 'audiobook' or 'ebook'
        // bookFile.mediaType is 'audiobook' or 'ebook' (from BookFile.DetermineMediaType)
        const mediaTypeFilter = selectedMediaType === 'audiobook' ? 'audiobook' : 'ebook';
        items = items.filter(file => 
          file.mediaType === mediaTypeFilter
        );
      }
      
      // Apply search filter
      if (filterValue && filterValue.trim()) {
        const searchTerm = filterValue.toLowerCase().trim();
        items = items.filter(file => {
          // Search in path (includes filename and extension)
          const pathMatch = file.path && file.path.toLowerCase().includes(searchTerm);
          // Search in book title
          const titleMatch = file.book && file.book.title && file.book.title.toLowerCase().includes(searchTerm);
          // Search in author name
          const authorMatch = file.book && file.book.authorTitle && file.book.authorTitle.toLowerCase().includes(searchTerm);
          
          return pathMatch || titleMatch || authorMatch;
        });
      }
      
      return {
        ...schema,
        items,
        ...otherProps,
        isDeleting: bookFiles.isDeleting,
        isSaving: bookFiles.isSaving,
        selectedMediaType,
        filterValue
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onSortPress(sortKey) {
      dispatch(setBookFilesSort({ sortKey }));
    },

    dispatchFetchQualityProfileSchema(name, path) {
      dispatch(fetchQualityProfileSchema());
    },

    dispatchUpdateBookFiles(updateProps) {
      dispatch(updateBookFiles(updateProps));
    },

    onDeletePress(bookFileIds) {
      dispatch(deleteBookFiles({ bookFileIds }));
    },

    dispatchDeleteBookFile(id) {
      dispatch(deleteBookFile(id));
    }
  };
}

class BookFileEditorTableContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchQualityProfileSchema();
  }

  //
  // Listeners

  onQualityChange = (bookFileIds, qualityId) => {
    const quality = {
      quality: _.find(this.props.qualities, { id: qualityId }),
      revision: {
        version: 1,
        real: 0
      }
    };

    this.props.dispatchUpdateBookFiles({ bookFileIds, quality });
  };

  //
  // Render

  render() {
    const {
      dispatchFetchQualityProfileSchema,
      dispatchUpdateBookFiles,
      ...otherProps
    } = this.props;

    return (
      <BookFileEditorTableContent
        {...otherProps}
        onQualityChange={this.onQualityChange}
      />
    );
  }
}

BookFileEditorTableContentConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  bookId: PropTypes.number,
  selectedMediaType: PropTypes.string,
  filterValue: PropTypes.string,
  qualities: PropTypes.arrayOf(PropTypes.object).isRequired,
  dispatchFetchQualityProfileSchema: PropTypes.func.isRequired,
  dispatchUpdateBookFiles: PropTypes.func.isRequired,
  dispatchDeleteBookFile: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, createMapDispatchToProps)(BookFileEditorTableContentConnector);
