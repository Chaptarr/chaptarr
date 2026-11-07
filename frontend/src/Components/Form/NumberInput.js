import PropTypes from 'prop-types';
import React, { Component } from 'react';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { getLocale } from 'Utilities/Localization/locale';
import TextInput from './TextInput';

const { formatNumber, parseLocalizedNumber, stepNumber } = intlFormatters;

function formatInputValue(value) {
  return value == null ? '' : formatNumber(value, {
    useGrouping: false,
    maximumFractionDigits: 20
  }, getLocale());
}

function parseValue(props, value) {
  const {
    isFloat,
    min,
    max
  } = props;

  if (value == null || value === '') {
    return null;
  }

  let newValue = parseLocalizedNumber(value, {
    locale: getLocale(),
    isFloat
  });

  if (newValue == null) {
    return null;
  }

  if (min != null && newValue < min) {
    newValue = min;
  } else if (max != null && newValue > max) {
    newValue = max;
  }

  return newValue;
}

class NumberInput extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      value: formatInputValue(props.value),
      isFocused: false
    };
  }

  componentDidUpdate(prevProps) {
    const { value } = this.props;

    if (!isNaN(value) && value !== prevProps.value && !this.state.isFocused) {
      this.setState({ value: formatInputValue(value) });
    }
  }

  onChange = ({ name, value }) => {
    this.setState({ value });

    this.props.onChange({
      name,
      value: parseValue(this.props, value)
    });
  };

  onFocus = () => {
    this.setState({ isFocused: true });
  };

  onKeyDown = (event) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') {
      return;
    }

    if (this.props.readOnly || this.props.isDisabled || this.props.disabled) {
      return;
    }

    event.preventDefault();

    const direction = event.key === 'ArrowUp' ? 1 : -1;
    const currentValue = parseValue(this.props, this.state.value);
    const nextValue = stepNumber(currentValue, direction, {
      step: this.props.step,
      min: this.props.min,
      max: this.props.max
    });

    if (nextValue == null) {
      return;
    }

    this.setState({ value: formatInputValue(nextValue) });
    this.props.onChange({
      name: this.props.name,
      value: nextValue
    });
  };

  onBlur = () => {
    const { name, onChange } = this.props;
    const { value } = this.state;
    const parsedValue = parseValue(this.props, value);

    this.setState({
      value: formatInputValue(parsedValue),
      isFocused: false
    });

    onChange({
      name,
      value: parsedValue
    });
  };

  render() {
    return (
      <TextInput
        {...this.props}
        type="text"
        inputMode={this.props.isFloat ? 'decimal' : 'numeric'}
        role="spinbutton"
        ariaValueMin={this.props.min}
        ariaValueMax={this.props.max}
        ariaValueNow={parseValue(this.props, this.state.value) ?? undefined}
        ariaValueText={this.state.value || undefined}
        value={this.state.value}
        onChange={this.onChange}
        onKeyDown={this.onKeyDown}
        onBlur={this.onBlur}
        onFocus={this.onFocus}
      />
    );
  }
}

NumberInput.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.number,
  min: PropTypes.number,
  max: PropTypes.number,
  step: PropTypes.number,
  isDisabled: PropTypes.bool,
  disabled: PropTypes.bool,
  readOnly: PropTypes.bool,
  isFloat: PropTypes.bool.isRequired,
  onChange: PropTypes.func.isRequired
};

NumberInput.defaultProps = {
  value: null,
  isFloat: false
};

export default NumberInput;
