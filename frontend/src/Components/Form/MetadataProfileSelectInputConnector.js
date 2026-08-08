import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { metadataProfileNames } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import sortByName from 'Utilities/Array/sortByName';
import translate from 'Utilities/String/translate';
import SelectInput from './SelectInput';

class MetadataProfileSelectInputConnector extends Component {

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

    if (prevProps.value !== this.props.value) {
      this.checkAndSetDefaultValue();
    }
  }

  fetchProfiles = () => {
    const { profileType } = this.props;

    this.setState({ isFetching: true });
    
    let url = '/metadataprofile';
    if (profileType) {
      url += `?mediaType=${encodeURIComponent(profileType)}`;
    }
    
    const { request } = createAjaxRequest({
      url,
      traditional: true
    });

    request.done((profiles) => {
      this.setState({
        profiles: profiles.sort(sortByName),
        isFetching: false 
      });

      setTimeout(() => this.checkAndSetDefaultValue(), 0);
    });

    request.fail((xhr) => {
      console.error('Failed to fetch metadata profiles:', xhr);
      this.setState({ isFetching: false });
    });
  };

  checkAndSetDefaultValue = () => {
    const {
      name,
      value
    } = this.props;

    if (value === 'noChange' || value === 'mixed') {
      return;
    }

    const values = this.getSelectValues();

    // Check if value exists in the values array (handle both string and number comparisons)
    const valueStr = value?.toString();
    const valueExists = values.some((option) => option.key === valueStr);

    if (value == null || value === 0 || !valueExists) {
      const firstValue = values.find((option) => !isNaN(parseInt(option.key, 10)));

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
      includeNone
    } = this.props;

    const { profiles } = this.state;
    
    // Filter out "None" profile from regular list (we'll add it separately if needed)
    const filteredProfiles = profiles.filter((item) => item.name !== metadataProfileNames.NONE);
    const noneProfile = profiles.find((item) => item.name === metadataProfileNames.NONE);
    
    const values = _.map(filteredProfiles, (metadataProfile) => {
      return {
        key: metadataProfile.id.toString(),
        value: metadataProfile.name
      };
    });

    if (includeNone && noneProfile) {
      values.push({
        key: noneProfile.id.toString(),
        value: noneProfile.name
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
        value: '(Mixed)',
        isDisabled: true
      });
    }

    return values;
  };

  //
  // Listeners

  onChange = ({ name, value }) => {
    if (value === 'noChange' || value === 'mixed') {
      this.props.onChange({ name, value });
      return;
    }

    this.props.onChange({ name, value: parseInt(value, 10) });
  };

  //
  // Render

  render() {
    const {
      className,
      name,
      value,
      hasError,
      hasWarning,
      ...otherProps
    } = this.props;

    const { isFetching } = this.state;
    const selectValue = value ?? '';

    return (
      <SelectInput
        className={className}
        name={name}
        value={selectValue}
        values={this.getSelectValues()}
        hasError={hasError}
        hasWarning={hasWarning}
        onChange={this.onChange}
        isDisabled={isFetching}
        {...otherProps}
      />
    );
  }
}

MetadataProfileSelectInputConnector.propTypes = {
  className: PropTypes.string,
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  values: PropTypes.arrayOf(PropTypes.object),
  hasError: PropTypes.bool,
  hasWarning: PropTypes.bool,
  includeNoChange: PropTypes.bool.isRequired,
  includeNoChangeDisabled: PropTypes.bool,
  includeMixed: PropTypes.bool.isRequired,
  includeNone: PropTypes.bool.isRequired,
  profileType: PropTypes.string,
  onChange: PropTypes.func.isRequired
};

MetadataProfileSelectInputConnector.defaultProps = {
  includeNoChange: false,
  includeMixed: false,
  includeNone: false
};

export default connect()(MetadataProfileSelectInputConnector);
