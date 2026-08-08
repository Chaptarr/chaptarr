import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import TextInput from 'Components/Form/TextInput';
import { icons } from 'Helpers/Props';
import { setAuthorDetailsFilterValue } from 'Store/Actions/authorDetailsActions';
import styles from './FilterSearchInput.css';

class FilterSearchInput extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFocused: false
    };
  }

  componentDidUpdate(prevProps) {
    // Clear filter when navigating to a different author
    if (prevProps.authorId !== this.props.authorId && this.props.filterValue) {
      this.props.onFilterChange('');
    }
  }

  //
  // Listeners

  onFocus = () => {
    this.setState({ isFocused: true });
  };

  onBlur = () => {
    this.setState({ isFocused: false });
  };

  onClearPress = () => {
    this.props.onFilterChange('');
  };

  onFilterChange = (payload) => {
    // TextInput passes {name, value} object, but we need just the value
    const value = typeof payload === 'object' ? payload.value : payload;
    this.props.onFilterChange(value);
  };

  //
  // Render

  render() {
    const {
      filterValue
    } = this.props;

    const {
      isFocused
    } = this.state;

    const hasValue = !!filterValue;

    return (
      <div className={styles.searchContainer}>
        <Icon
          className={styles.searchIcon}
          name={icons.SEARCH}
        />
        <TextInput
          className={styles.searchInput}
          name="authorDetailsFilter"
          value={filterValue}
          placeholder="Search books, series, narrators..."
          onChange={this.onFilterChange}
          onFocus={this.onFocus}
          onBlur={this.onBlur}
        />
        {hasValue &&
          <IconButton
            className={styles.clearIcon}
            name={icons.REMOVE}
            title="Clear filter"
            onPress={this.onClearPress}
          />
        }
      </div>
    );
  }
}

FilterSearchInput.propTypes = {
  filterValue: PropTypes.string.isRequired,
  onFilterChange: PropTypes.func.isRequired,
  authorId: PropTypes.number.isRequired
};

function createMapStateToProps() {
  return (state) => {
    return {
      filterValue: state.authorDetails.filterValue || ''
    };
  };
}

const mapDispatchToProps = {
  onFilterChange: setAuthorDetailsFilterValue
};

export default connect(createMapStateToProps, mapDispatchToProps)(FilterSearchInput);
