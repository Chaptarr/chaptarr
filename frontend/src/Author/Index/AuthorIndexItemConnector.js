/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { executeCommand } from 'Store/Actions/commandActions';
import createAuthorMetadataProfileSelector from 'Store/Selectors/createAuthorMetadataProfileSelector';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createExecutingCommandsSelector from 'Store/Selectors/createExecutingCommandsSelector';
import { getAuthorMediaTypeMonitoringStatus, isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import { getAuthorSearchBookCount, getAuthorStatisticsForMediaType, shouldConfirmSearch } from 'Utilities/Author/getAuthorStatisticsForMediaType';
import translate from 'Utilities/String/translate';

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

      const statistics = getAuthorStatisticsForMediaType(author, selectedMediaType);

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
      const path = profileMediaType === 'ebook' ? author.ebookFolder : author.audiobookFolder;

      return {
        ...author,
        monitored: isAuthorMonitoredForSelection(author, selectedMediaType),
        statistics,
        qualityProfile,
        path: path || '',
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
  state = { pendingSearch: null };

  //
  // Listeners

  onRefreshAuthorPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.REFRESH_AUTHOR,
      authorId: this.props.id
    });
  };

  onSearchPress = () => {
    const count = getAuthorSearchBookCount(this.props);
    if (shouldConfirmSearch(count)) {
      this.setState({ pendingSearch: { authorId: this.props.id, count } });
      return;
    }

    this.onSearchConfirmed();
  };

  onSearchConfirmed = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.AUTHOR_SEARCH,
      authorId: this.props.id
    });
    this.onSearchCancelled();
  };

  onSearchCancelled = () => {
    this.setState({ pendingSearch: null });
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
      <>
        <ItemComponent
          {...otherProps}
          id={id}
          onRefreshAuthorPress={this.onRefreshAuthorPress}
          onSearchPress={this.onSearchPress}
        />
        <ConfirmModal
          isOpen={this.state.pendingSearch?.authorId === this.props.id}
          title={translate('SearchMonitored')}
          message={translate('SearchAuthorBooksConfirm', { count: this.state.pendingSearch?.count ?? 0 })}
          confirmLabel={translate('Search')}
          onConfirm={this.onSearchConfirmed}
          onCancel={this.onSearchCancelled}
        />
      </>
    );
  }
}

AuthorIndexItemConnector.propTypes = {
  id: PropTypes.number,
  component: PropTypes.elementType.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorIndexItemConnector);
