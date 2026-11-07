import classNames from 'classnames';
import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import { icons } from 'Helpers/Props';
import translateServerMessage from 'Utilities/String/translateServerMessage';
import styles from './FormInputHelpText.css';

function FormInputHelpText(props) {
  const {
    className,
    id,
    text,
    link,
    tooltip,
    isError,
    isWarning,
    isCheckInput
  } = props;

  return (
    <div
      id={id}
      className={classNames(
        className,
        isError && styles.isError,
        isWarning && styles.isWarning,
        isCheckInput && styles.isCheckInput
      )}
    >
      {translateServerMessage(text)}

      {
        link ?
          <Link
            className={styles.link}
            to={link}
            title={translateServerMessage(tooltip)}
          >
            <Icon
              name={icons.EXTERNAL_LINK}
            />
          </Link> :
          null
      }

      {
        !link && tooltip ?
          <Icon
            containerClassName={styles.details}
            name={icons.INFO}
            title={translateServerMessage(tooltip)}
          /> :
          null
      }
    </div>
  );
}

FormInputHelpText.propTypes = {
  className: PropTypes.string.isRequired,
  id: PropTypes.string,
  text: PropTypes.string.isRequired,
  link: PropTypes.string,
  tooltip: PropTypes.string,
  isError: PropTypes.bool,
  isWarning: PropTypes.bool,
  isCheckInput: PropTypes.bool
};

FormInputHelpText.defaultProps = {
  className: styles.helpText,
  isError: false,
  isWarning: false,
  isCheckInput: false
};

export default FormInputHelpText;
