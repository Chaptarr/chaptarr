import isEqual from 'lodash/isEqual';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearOptions, defaultState, fetchOptions } from 'Store/Actions/providerOptionActions';
import translate from 'Utilities/String/translate';
import EnhancedSelectInput from './EnhancedSelectInput';

const importantFieldNames = [
  'baseUrl',
  'serverUrl', // Legacy provider settings
  'apiPath',
  'apiKey',
  'authToken',
  'host',
  'port',
  'useSsl',
  'urlBase',
  'username', // For authentication
  'password' // For authentication (Deluge, etc.)
];

function getProviderDataKey(providerData) {
  if (!providerData || !providerData.fields) {
    return null;
  }

  const fields = providerData.fields
    .filter((f) => importantFieldNames.includes(f.name))
    .map((f) => f.value);

  return fields;
}

function getSelectOptions(items) {
  if (!items) {
    return [];
  }

  return items.map((option) => {
    return {
      key: option.value,
      value: option.localizationKey ? translate(option.localizationKey) : option.name,
      hint: option.hint,
      parentKey: option.parentValue,
      isDisabled: option.isDisabled,
      isHidden: option.isHidden == null ? option.isDisabled : option.isHidden,
      additionalProperties: option.additionalProperties
    };
  });
}

function createMapStateToProps() {
  return createSelector(
    (state, { selectOptionsProviderAction }) => state.providerOptions[selectOptionsProviderAction] || defaultState,
    (options) => {
      if (options) {
        return {
          isFetching: options.isFetching,
          values: getSelectOptions(options.items),
          selectOption: options.selectOption,
          fetchError: options.error
        };
      }
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchOptions: fetchOptions,
  dispatchClearOptions: clearOptions
};

class EnhancedSelectInputConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      refetchRequired: false
    };
  }

  componentDidMount = () => {
    this._populate();
  };

  componentDidUpdate = (prevProps) => {
    const prevKey = getProviderDataKey(prevProps.providerData);
    const nextKey = getProviderDataKey(this.props.providerData);

    if (!isEqual(prevKey, nextKey)) {
      this.setState({ refetchRequired: true });
      // Immediately populate when provider data changes (e.g., API key entered)
      this._populate();
    }

    // If we just received a selectOption and don't have a current value, set it
    if (this.props.selectOption && prevProps.selectOption !== this.props.selectOption) {
      const { value } = this.props;

      // Check if this is a single-select (string) or multi-select (array)
      const isMultiSelect = Array.isArray(value);

      // Only set if the field is empty or has no selection
      if (isMultiSelect) {
        if (!value || value.length === 0) {
          this.onChange({ name: this.props.name, value: [this.props.selectOption] });
        }
      } else if (value === undefined || value === null || value === '') {
        // Check for empty string, null, undefined
        this.onChange({ name: this.props.name, value: this.props.selectOption });
      }
    }

    // Also check if selectOption exists but value is empty (for initial load)
    if (this.props.selectOption && !prevProps.selectOption && this.props.values.length > 0) {
      const { value } = this.props;
      if (value === undefined || value === null || value === '') {
        this.onChange({ name: this.props.name, value: this.props.selectOption });
      }
    }
  };

  componentWillUnmount = () => {
    this._cleanup();
  };

  //
  // Listeners

  onOpen = () => {
    if (this.state.refetchRequired) {
      this._populate();
    }
  };

  onChange = ({ name, value }) => {
    const selectedOption = this.props.values.find((option) => `${option.key}` === `${value}`);

    this.props.onChange({ name, value });

    if (!selectedOption || !selectedOption.additionalProperties) {
      return;
    }

    Object.keys(selectedOption.additionalProperties).forEach((key) => {
      this.props.onChange({
        name: key,
        value: selectedOption.additionalProperties[key]
      });
    });
  };

  //
  // Control

  _populate() {
    const {
      provider,
      providerData,
      selectOptionsProviderAction,
      dispatchFetchOptions
    } = this.props;

    if (selectOptionsProviderAction) {
      this.setState({ refetchRequired: false });
      dispatchFetchOptions({
        section: selectOptionsProviderAction,
        action: selectOptionsProviderAction,
        provider,
        providerData
      });
    }
  }

  _cleanup() {
    const {
      selectOptionsProviderAction,
      dispatchClearOptions
    } = this.props;

    if (selectOptionsProviderAction) {
      dispatchClearOptions({ section: selectOptionsProviderAction });
    }
  }

  //
  // Render

  render() {
    return (
      <EnhancedSelectInput
        {...this.props}
        onChange={this.onChange}
        onOpen={this.onOpen}
      />
    );
  }
}

EnhancedSelectInputConnector.propTypes = {
  provider: PropTypes.string.isRequired,
  providerData: PropTypes.object.isRequired,
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([
    PropTypes.arrayOf(PropTypes.oneOfType([PropTypes.number, PropTypes.string])),
    PropTypes.string,
    PropTypes.number
  ]),
  values: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectOptionsProviderAction: PropTypes.string,
  selectOption: PropTypes.string,
  fetchError: PropTypes.object,
  onChange: PropTypes.func.isRequired,
  isFetching: PropTypes.bool.isRequired,
  dispatchFetchOptions: PropTypes.func.isRequired,
  dispatchClearOptions: PropTypes.func.isRequired
};

EnhancedSelectInputConnector.defaultProps = {
  value: ''
};

export default connect(createMapStateToProps, mapDispatchToProps)(EnhancedSelectInputConnector);
