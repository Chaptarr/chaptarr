import map from 'lodash/map';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import sortByName from 'Utilities/Array/sortByName';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import SelectInput from './SelectInput';

class QualityProfileSelectInputConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      profiles: [],
      isFetching: false
    };
  }

  componentDidMount() {
    this.fetchProfiles();
  }

  componentDidUpdate(prevProps) {
    // Re-fetch if profileType changed
    if (prevProps.profileType !== this.props.profileType) {
      this.fetchProfiles();
    }

    // Re-check if value changed or profiles list changed
    if (prevProps.value !== this.props.value) {
      this.checkAndSetDefaultValue();
    }
  }

  fetchProfiles = () => {
    const { profileType } = this.props;

    this.setState({ isFetching: true });

    let url = '/qualityprofile';
    if (profileType) {
      url += `?mediaType=${encodeURIComponent(profileType)}`;
    }

    const { request } = createAjaxRequest({
      url,
      traditional: true
    });

    request.then((profiles) => {
      this.setState({
        profiles: profiles.sort(sortByName),
        isFetching: false
      }, this.checkAndSetDefaultValue);
    });

    request.catch((xhr) => {
      console.error('Failed to fetch quality profiles:', xhr);
      this.setState({ isFetching: false });
    });
  };

  checkAndSetDefaultValue = () => {
    const {
      name,
      value,
      includeNone,
      noneValue
    } = this.props;

    const values = this.getSelectValues();

    // Don't auto-select if includeNone is true and value is explicitly set
    if (includeNone && (value === null || value === undefined || value === noneValue)) {
      return;
    }

    // Check if value exists in the values array (handle both string and number comparisons)
    const valueStr = value?.toString();
    const valueExists = values.some((option) => {
      return option.key === valueStr || option.key === value ||
             (typeof value === 'number' && parseInt(option.key) === value);
    });

    if (!value || value === 0 || !valueExists) {
      const firstValue = values.find((option) => !isNaN(parseInt(option.key)));

      if (firstValue) {
        this.onChange({ name, value: firstValue.key });
      }
    }
  };

  getSelectValues = () => {
    const {
      includeNoChange,
      includeNoChangeDisabled = true,
      includeMixed,
      includeNone,
      noneLabel,
      noneDisabled = false
    } = this.props;

    const { profiles } = this.state;

    const values = map(profiles, (qualityProfile) => {
      return {
        key: qualityProfile.id.toString(),
        value: qualityProfile.name
      };
    });

    if (includeNone) {
      values.unshift({
        key: 'none',
        value: noneLabel || translate('None'),
        isDisabled: noneDisabled
      });
    }

    if (includeNoChange) {
      values.unshift({
        key: 'noChange',
        value: translate('NoChange'),
        isDisabled: includeNoChangeDisabled
      });
    }

    if (includeMixed) {
      values.unshift({
        key: 'mixed',
        value: `(${translate('Mixed')})`,
        isDisabled: true
      });
    }

    return values;
  };

  //
  // Listeners

  onChange = ({ name, value }) => {
    if (value === 'noChange') {
      this.props.onChange({ name, value });
    } else if (value === 'none') {
      this.props.onChange({ name, value: this.props.noneValue });
    } else {
      this.props.onChange({ name, value: parseInt(value) });
    }
  };

  //
  // Render

  render() {
    const { isFetching } = this.state;
    const { includeNone, noneValue, value } = this.props;
    const selectValue = includeNone && (value == null || value === noneValue) ? 'none' : value;

    return (
      <SelectInput
        {...this.props}
        value={selectValue}
        values={this.getSelectValues()}
        onChange={this.onChange}
        isDisabled={this.props.isDisabled || isFetching}
      />
    );
  }
}

QualityProfileSelectInputConnector.propTypes = {
  className: PropTypes.string,
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  values: PropTypes.arrayOf(PropTypes.object),
  hasError: PropTypes.bool,
  hasWarning: PropTypes.bool,
  isDisabled: PropTypes.bool,
  includeNoChange: PropTypes.bool.isRequired,
  includeNoChangeDisabled: PropTypes.bool,
  includeMixed: PropTypes.bool.isRequired,
  includeNone: PropTypes.bool.isRequired,
  noneLabel: PropTypes.string,
  noneDisabled: PropTypes.bool,
  noneValue: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  profileType: PropTypes.string,
  onChange: PropTypes.func.isRequired
};

QualityProfileSelectInputConnector.defaultProps = {
  includeNoChange: false,
  includeMixed: false,
  includeNone: false,
  noneValue: 'none'
};

export default connect()(QualityProfileSelectInputConnector);
