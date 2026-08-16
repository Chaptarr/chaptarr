/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import createAuthorMetadataProfileSelector from 'Store/Selectors/createAuthorMetadataProfileSelector';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createExecutingCommandsSelector from 'Store/Selectors/createExecutingCommandsSelector';
import { getAuthorMediaTypeMonitoringStatus, isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';

function selectShowSearchAction() {
  return createSelector(
    (state) => state.authorIndex,
    (authorIndex) => {
      const view = authorIndex.view;

      switch (view) {
        case 'posters':
          return authorIndex.posterOptions.showSearchAction;
        case 'banners':
          return authorIndex.bannerOptions.showSearchAction;
        case 'overview':
          return authorIndex.overviewOptions.showSearchAction;
        default:
          return authorIndex.tableOptions.showSearchAction;
      }
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    (state) => state.settings.qualityProfiles.items,
    createAuthorMetadataProfileSelector(),
    selectShowSearchAction(),
    createExecutingCommandsSelector(),
    // IMPORTANT: include ownProps to pick media-type-specific stats
    (state, ownProps) => ownProps && ownProps.selectedMediaType,
    (
      author,
      qualityProfiles,
      metadataProfile,
      showSearchAction,
      executingCommands,
      selectedMediaType
    ) => {

      // If an author is deleted this selector may fire before the parent
      // selectors, which will result in an undefined author, if that happens
      // we want to return early here and again in the render function to avoid
      // trying to show an author that has no information available.

      if (!author) {
        return {};
      }

      const isRefreshingAuthor = executingCommands.some((command) => {
        return (
          command.name === commandNames.REFRESH_AUTHOR &&
          command.body.authorId === author.id
        );
      });

      const isSearchingAuthor = executingCommands.some((command) => {
        return (
          command.name === commandNames.AUTHOR_SEARCH &&
          command.body.authorId === author.id
        );
      });

      const latestBook = author.books && author.books.length > 0 ?
        _.maxBy(author.books, (book) => book.releaseDate) :
        null;

      let statistics = author.statistics;
      if (selectedMediaType === 'audiobook' && author.audiobookStatistics) {
        statistics = author.audiobookStatistics;
      } else if (selectedMediaType === 'ebook' && author.ebookStatistics) {
        statistics = author.ebookStatistics;
      }

      const profileById = (id) => (qualityProfiles || []).find((profile) => profile.id === id);
      const mediaTypeDetails = [
        {
          ...getAuthorMediaTypeMonitoringStatus(author, 'audiobook'),
          qualityProfile: profileById(author.audiobookQualityProfileId)
        },
        {
          ...getAuthorMediaTypeMonitoringStatus(author, 'ebook'),
          qualityProfile: profileById(author.ebookQualityProfileId)
        }
      ];
      const profileMediaType = selectedMediaType === 'audiobook' || selectedMediaType === 'ebook' ?
        selectedMediaType :
        author.lastSelectedMediaType;
      const qualityProfile = mediaTypeDetails.find((details) => details.mediaType === profileMediaType)?.qualityProfile;

      return {
        ...author,
        monitored: isAuthorMonitoredForSelection(author, selectedMediaType),
        statistics,
        qualityProfile,
        mediaTypeDetails,
        metadataProfile,
        latestBook,
        showSearchAction,
        isRefreshingAuthor,
        isSearchingAuthor
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchExecuteCommand: executeCommand
};

class AuthorIndexItemConnector extends Component {

  //
  // Listeners

  onRefreshAuthorPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.REFRESH_AUTHOR,
      authorId: this.props.id
    });
  };

  onSearchPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.AUTHOR_SEARCH,
      authorId: this.props.id
    });
  };

  //
  // Render

  render() {
    const {
      id,
      component: ItemComponent,
      ...otherProps
    } = this.props;

    if (!id) {
      return null;
    }

    return (
      <ItemComponent
        {...otherProps}
        id={id}
        onRefreshAuthorPress={this.onRefreshAuthorPress}
        onSearchPress={this.onSearchPress}
      />
    );
  }
}

AuthorIndexItemConnector.propTypes = {
  id: PropTypes.number,
  component: PropTypes.elementType.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorIndexItemConnector);
