import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { metadataProfileNames } from 'Helpers/Props';
import sortByName from 'Utilities/Array/sortByName';
import translate from 'Utilities/String/translate';
import SelectInput from './SelectInput';

const profileTypes = { general: 0, audiobook: 1, ebook: 2 };

function createMapStateToProps() {
  return createSelector(
    (state, { profileKind }) => state.settings[`${profileKind}Profiles`],
    (state, { profileType }) => profileType,
    (section, profileType) => {
      const requestedType = profileTypes[profileType?.toLowerCase()];
      return {
        ...section,
        profiles: [...section.items].sort(sortByName).map((profile) => {
          const type = profileTypes[`${profile.profileType}`.toLowerCase()] ?? Number(profile.profileType ?? 0);
          return { ...profile, compatible: !requestedType || type === 0 || type === requestedType };
        })
      };
    }
  );
}

class ProfileSelectInputConnector extends Component {
  componentDidMount() {
    this.setInitialValue();
  }

  componentDidUpdate() {
    this.setInitialValue();
  }

  setInitialValue = () => {
    const { name, value, includeNone, profileKind, isPopulated, isFetching, error } = this.props;
    // Missing saved IDs and sentinel choices must never become a different profile.
    if (!isPopulated || isFetching || error || (value != null && value !== '' && value !== 0) ||
        (includeNone && profileKind === 'quality')) {
      return;
    }

    const first = this.getValues().find((option) => !option.isDisabled && (/^\d+$/).test(option.key));
    if (first) {
      this.onChange({ name, value: first.key });
    }
  };

  getValues = () => {
    const { profiles, profileKind, value, includeNone, noneLabel, includeNoChange,
      includeNoChangeDisabled, includeMixed } = this.props;
    const values = profiles.filter((profile) =>
      (profile.compatible && (profileKind !== 'metadata' || includeNone || profile.name !== metadataProfileNames.NONE)) ||
      `${profile.id}` === `${value}`
    ).sort((a, b) => (profileKind === 'metadata' ?
      Number(a.name === metadataProfileNames.NONE) - Number(b.name === metadataProfileNames.NONE) : 0)
    ).map((profile) => ({
      key: `${profile.id}`,
      value: profile.name,
      isDisabled: !profile.compatible
    }));

    if (includeNone && profileKind === 'quality') {
      values.unshift({ key: 'none', value: noneLabel || translate('None') });
    }
    if (includeNoChange) {
      values.unshift({ key: 'noChange', value: translate('NoChange'), isDisabled: includeNoChangeDisabled });
    }
    if (includeMixed) {
      values.unshift({ key: 'mixed', value: `(${translate('Mixed')})`, isDisabled: true });
    }

    const selected = value ?? (includeNone && profileKind === 'quality' ? 'none' : '');
    if (!values.some((option) => option.key === `${selected}`)) {
      values.unshift({ key: `${selected}`, value: selected ? `${translate('Unknown')} (${selected})` : '', isDisabled: true });
    }
    return values;
  };

  onChange = ({ name, value }) => {
    this.props.onChange({ name, value: (/^\d+$/).test(value) ? Number(value) : value });
  };

  render() {
    const { value, includeNone, profileKind, isDisabled, isFetching, isPopulated, error } = this.props;
    return (
      <SelectInput
        {...this.props}
        value={value ?? (includeNone && profileKind === 'quality' ? 'none' : '')}
        values={this.getValues()}
        onChange={this.onChange}
        isDisabled={isDisabled || isFetching || !isPopulated}
        hasWarning={this.props.hasWarning || !!error}
      />
    );
  }
}

ProfileSelectInputConnector.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  profileKind: PropTypes.oneOf(['quality', 'metadata']).isRequired,
  profileType: PropTypes.string,
  profiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  isPopulated: PropTypes.bool,
  isFetching: PropTypes.bool,
  error: PropTypes.object,
  isDisabled: PropTypes.bool,
  hasWarning: PropTypes.bool,
  includeNone: PropTypes.bool,
  noneLabel: PropTypes.string,
  includeNoChange: PropTypes.bool,
  includeNoChangeDisabled: PropTypes.bool,
  includeMixed: PropTypes.bool,
  onChange: PropTypes.func.isRequired
};

ProfileSelectInputConnector.defaultProps = {
  includeNone: false,
  includeNoChange: false,
  includeNoChangeDisabled: true,
  includeMixed: false
};

export default connect(createMapStateToProps)(ProfileSelectInputConnector);
