import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchProxies } from 'Store/Actions/settingsActions';
import sortByName from 'Utilities/Array/sortByName';
import translate from 'Utilities/String/translate';
import EnhancedSelectInput from './EnhancedSelectInput';

function getSettingValue(setting) {
  if (setting && typeof setting === 'object' && Object.prototype.hasOwnProperty.call(setting, 'value')) {
    return setting.value;
  }

  return setting;
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.general.pendingChanges.proxyMode ?? state.settings.general.item.proxyMode,
    (state) => state.settings.proxies,
    (state, { includeNone }) => includeNone,
    (state, { includeDirectConnection }) => includeDirectConnection,
    (proxyMode, proxies, includeNone, includeDirectConnection) => {
      const proxyModeValue = getSettingValue(proxyMode);
      const isProxyEnabled = proxyModeValue && (
        proxyModeValue === 'indexerOnly' || proxyModeValue === 'proxyEverything' ||
        proxyModeValue === 'IndexerOnly' || proxyModeValue === 'ProxyEverything'
      );

      const values = [];

      if (includeNone) {
        values.push({
          key: 'null',
          value: isProxyEnabled ? 'Use default proxy' : 'Proxy disabled'
        });
      }

      if (includeDirectConnection) {
        values.push({
          key: -1,
          value: 'Direct connection',
          hint: 'Do not use a proxy for this indexer'
        });
      }

      if (isProxyEnabled && proxies && proxies.items && proxies.items.length > 0) {
        const proxyValues = _.map([...proxies.items].sort(sortByName), (proxy) => {
          return {
            key: proxy.id,
            value: proxy.name,
            hint: `${proxy.hostname}:${proxy.port}`
          };
        });
        values.push(...proxyValues);
      }

      return {
        values,
        isDisabled: !isProxyEnabled,
        isFetching: proxies && proxies.isFetching,
        isPopulated: proxies && proxies.isPopulated,
        error: proxies && proxies.error
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchProxies: fetchProxies
};

class ProxySelectInputConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchProxies();
  }

  //
  // Listeners

  onChange = ({ name, value }) => {
    // Handle default/direct proxy sentinel values.
    const parsedValue = value === null || value === 'null' ? null : parseInt(value);
    this.props.onChange({ name, value: parsedValue });
  };

  onFocus = () => {
    this.props.dispatchFetchProxies();
  };

  //
  // Render

  render() {
    const {
      isFetching,
      error,
      value,
      ...otherProps
    } = this.props;

    if (error) {
      return (
        <div>
          {translate('ProxySelectErrorLoading', { message: error.message || error })}
        </div>
      );
    }

    // Ensure value is never undefined - default to null for "None" option
    const safeValue = value === undefined ? null : value;

    return (
      <EnhancedSelectInput
        {...otherProps}
        value={safeValue}
        onChange={this.onChange}
        onFocus={this.onFocus}
        isDisabled={otherProps.isDisabled || isFetching}
      />
    );
  }
}

ProxySelectInputConnector.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  values: PropTypes.arrayOf(PropTypes.object).isRequired,
  includeNone: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onChange: PropTypes.func.isRequired,
  dispatchFetchProxies: PropTypes.func.isRequired,
  includeDirectConnection: PropTypes.bool
};

ProxySelectInputConnector.defaultProps = {
  includeNone: true,
  includeDirectConnection: false
};

export default connect(createMapStateToProps, mapDispatchToProps)(ProxySelectInputConnector);
