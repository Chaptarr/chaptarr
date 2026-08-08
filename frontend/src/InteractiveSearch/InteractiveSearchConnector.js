import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as releaseActions from 'Store/Actions/releaseActions';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import InteractiveSearch from './InteractiveSearch';

function createMapStateToProps(appState, { type }) {
  return createSelector(
    (state) => state.releases.filterSummary,
    (state) => state.releases.bypassFilters,
    createClientSideCollectionSelector('releases', `releases.${type}`),
    createUISettingsSelector(),
    (filterSummary, bypassFilters, releases, uiSettings) => {
      const totalReleasesCount = filterSummary?.totalResults ?? (releases.items.length + (releases.hiddenItems?.length ?? 0));

      return {
        totalReleasesCount,
        bypassFilters,
        filterSummary,
        longDateFormat: uiSettings.longDateFormat,
        timeFormat: uiSettings.timeFormat,
        ...releases
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    dispatchFetchReleases(payload) {
      dispatch(releaseActions.fetchReleases(payload));
    },

    dispatchCancelFetchReleases() {
      dispatch(releaseActions.cancelFetchReleases());
    },

    dispatchApplyReleaseSearchResponse(payload) {
      dispatch(releaseActions.setReleaseSearchResponse(payload));
    },

    onSortPress(sortKey, sortDirection) {
      dispatch(releaseActions.setReleasesSort({ sortKey, sortDirection }));
    },

    onFilterSelect(selectedFilterKey) {
      const action = props.type === 'book' ?
        releaseActions.setBookReleasesFilter :
        releaseActions.setAuthorReleasesFilter;

      dispatch(action({ selectedFilterKey }));
    },

    onGrabPress(payload) {
      dispatch(releaseActions.grabRelease(payload));
    },

    onToggleBypass(bypassFilters) {
      dispatch(releaseActions.setBypassFilters({ bypassFilters }));
    }
  };
}

class InteractiveSearchConnector extends Component {
  constructor(props) {
    super(props);

    this.responseCache = new Map();
  }

  componentDidMount() {
    this.loadSearch(this.props);
  }

  componentDidUpdate(prevProps) {
    const previousKey = this.getCacheKey(prevProps);
    const currentKey = this.getCacheKey(this.props);

    if (previousKey !== currentKey) {
      if (prevProps.isFetching) {
        this.props.dispatchCancelFetchReleases();
      }

      this.loadSearch(this.props);
      return;
    }

    if (this.shouldCacheResponse(prevProps, this.props)) {
      this.responseCache.set(currentKey, this.buildResponseSnapshot(this.props));
    }
  }

  componentWillUnmount() {
    this.props.dispatchCancelFetchReleases();
  }

  getCacheKey(props) {
    const { searchPayload, bypassFilters } = props;

    return [
      `book:${searchPayload.bookId ?? ''}`,
      `author:${searchPayload.authorId ?? ''}`,
      `mediaType:${searchPayload.initialMediaType ?? ''}`,
      `bypass:${bypassFilters ? 1 : 0}`
    ].join('|');
  }

  buildResponseSnapshot(props) {
    return {
      items: props.items,
      hiddenItems: props.hiddenItems,
      filterSummary: props.filterSummary,
      siblingBookId: props.siblingBookId,
      siblingMediaType: props.siblingMediaType,
      siblingToggleEnabled: props.siblingToggleEnabled,
      siblingToggleDisabledReason: props.siblingToggleDisabledReason
    };
  }

  shouldCacheResponse(prevProps, nextProps) {
    if (nextProps.isFetching || !nextProps.isPopulated || nextProps.error) {
      return false;
    }

    return prevProps.isFetching !== nextProps.isFetching ||
      prevProps.items !== nextProps.items ||
      prevProps.hiddenItems !== nextProps.hiddenItems ||
      prevProps.filterSummary !== nextProps.filterSummary ||
      prevProps.siblingBookId !== nextProps.siblingBookId ||
      prevProps.siblingMediaType !== nextProps.siblingMediaType ||
      prevProps.siblingToggleEnabled !== nextProps.siblingToggleEnabled ||
      prevProps.siblingToggleDisabledReason !== nextProps.siblingToggleDisabledReason;
  }

  loadSearch(props) {
    const cacheKey = this.getCacheKey(props);
    const cachedResponse = this.responseCache.get(cacheKey);

    if (cachedResponse) {
      props.dispatchApplyReleaseSearchResponse(cachedResponse);
      return;
    }

    props.dispatchFetchReleases(props.searchPayload);
  }

  render() {
    const {
      dispatchFetchReleases,
      dispatchCancelFetchReleases,
      dispatchApplyReleaseSearchResponse,
      ...otherProps
    } = this.props;

    return (
      <InteractiveSearch
        {...otherProps}
      />
    );
  }
}

InteractiveSearchConnector.propTypes = {
  searchPayload: PropTypes.object.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  hiddenItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  filterSummary: PropTypes.object,
  bypassFilters: PropTypes.bool,
  siblingBookId: PropTypes.number,
  siblingMediaType: PropTypes.string,
  siblingToggleEnabled: PropTypes.bool,
  siblingToggleDisabledReason: PropTypes.string,
  dispatchFetchReleases: PropTypes.func.isRequired,
  dispatchCancelFetchReleases: PropTypes.func.isRequired,
  dispatchApplyReleaseSearchResponse: PropTypes.func.isRequired,
  onMediaTypeChange: PropTypes.func
};

export default connect(createMapStateToProps, createMapDispatchToProps)(InteractiveSearchConnector);
