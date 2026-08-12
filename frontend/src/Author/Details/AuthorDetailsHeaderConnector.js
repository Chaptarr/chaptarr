/* eslint max-params: 0 */
import reduce from 'lodash/reduce';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleAuthorMonitored } from 'Store/Actions/authorActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import { getAuthorMonitorExistingValue } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import AuthorDetailsHeader from './AuthorDetailsHeader';

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors,
    createAuthorSelector(),
    createDimensionsSelector(),
    (state, props) => props.selectedMediaType,
    (authors, author, dimensions, selectedMediaTypeProp) => {
      if (!author) {
        return {};
      }

      const alternateTitles = reduce(author.alternateTitles, (acc, alternateTitle) => {
        if ((alternateTitle.seasonNumber === -1 || alternateTitle.seasonNumber === undefined) &&
            (alternateTitle.sceneSeasonNumber === -1 || alternateTitle.sceneSeasonNumber === undefined)) {
          acc.push(alternateTitle.title);
        }

        return acc;
      }, []);

      const selectedMediaType = selectedMediaTypeProp || author.lastSelectedMediaType || 'audiobook';
      const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, selectedMediaType);
      const effectiveMediaType = rootFolderStatus.mediaType;
      const hasRootFolder = rootFolderStatus.hasRootFolder;

      const qualityProfileId = effectiveMediaType === 'audiobook'
        ? author.audiobookQualityProfileId
        : author.ebookQualityProfileId;

      // CONTEXT-AWARE MONITORING: show monitoring status for current media type.
      // Tri-state values: 0=None, 1=All, 2=Selected.
      // When the author has no root folder for the selected media type, disable toggling and display "None".
      const isMonitored = getAuthorMonitorExistingValue(author, effectiveMediaType);
      
      // Use media-type specific size if available, otherwise fallback to total statistics
      const mediaTypeSize = effectiveMediaType === 'audiobook' 
        ? author.audiobookSizeOnDisk 
        : author.ebookSizeOnDisk;
      
      const statistics = {
        ...author.statistics,
        sizeOnDisk: mediaTypeSize !== undefined ? mediaTypeSize : (author.statistics ? author.statistics.sizeOnDisk : 0)
      };

      return {
        ...author,
        monitored: isMonitored, // Override with context-aware monitoring
        isMonitorToggleDisabled: !hasRootFolder,
        statistics,
        isSaving: authors.isSaving,
        alternateTitles,
        isSmallScreen: dimensions.isSmallScreen,
        selectedMediaType: effectiveMediaType,
        qualityProfileId
      };
    }
  );
}

const mapDispatchToProps = {
  toggleAuthorMonitored
};

class AuthorDetailsHeaderConnector extends Component {

  //
  // Listeners

  onMonitorTogglePress = (monitorValue) => {
    // CONTEXT-AWARE MONITORING: Toggle the correct media-type-specific field
    // monitorValue is now tri-state: 0=None, 1=All, 2=Selected
    const { selectedMediaType, isMonitorToggleDisabled } = this.props;

    if (isMonitorToggleDisabled) {
      return;
    }
    
    this.props.toggleAuthorMonitored({
      authorId: this.props.authorId,
      monitored: monitorValue,
      mediaType: selectedMediaType  // Pass media type for context-aware toggling
    });
  };

  //
  // Render

  render() {
    return (
      <AuthorDetailsHeader
        {...this.props}
        onMonitorTogglePress={this.onMonitorTogglePress}
      />
    );
  }
}

AuthorDetailsHeaderConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.string,
  isMonitorToggleDisabled: PropTypes.bool,
  toggleAuthorMonitored: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorDetailsHeaderConnector);
