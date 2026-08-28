/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleAuthorMonitored } from 'Store/Actions/authorActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import { getAuthorMonitoredValue } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
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

      const alternateTitles = _.reduce(author.alternateTitles, (acc, alternateTitle) => {
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

      const qualityProfileId = effectiveMediaType === 'audiobook' ?
        author.audiobookQualityProfileId :
        author.ebookQualityProfileId;

      // Show and edit the simple author-level gate for the current media type.
      const isMonitored = getAuthorMonitoredValue(author, effectiveMediaType);

      // Use media-type specific size if available, otherwise fallback to total statistics
      const mediaTypeSize = effectiveMediaType === 'audiobook' ? author.audiobookSizeOnDisk : author.ebookSizeOnDisk;
      const aggregateSizeOnDisk = author.statistics ? author.statistics.sizeOnDisk : 0;

      const statistics = {
        ...author.statistics,
        sizeOnDisk: mediaTypeSize ?? aggregateSizeOnDisk
      };

      return {
        ...author,
        monitored: isMonitored,
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
    const { selectedMediaType, isMonitorToggleDisabled } = this.props;

    if (isMonitorToggleDisabled) {
      return;
    }

    this.props.toggleAuthorMonitored({
      authorId: this.props.authorId,
      monitored: monitorValue,
      mediaType: selectedMediaType // Pass media type for context-aware toggling
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
