import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import { kinds } from 'Helpers/Props';
import styles from './Form.css';

function Form(props) {
  const {
    children,
    validationErrors,
    validationWarnings,
    successMessages,
    // eslint-disable-next-line no-unused-vars
    ...otherProps
  } = props;

  return (
    <div>
      {
        successMessages.length || validationErrors.length || validationWarnings.length ?
          <div className={styles.validationFailures}>
            {
              successMessages.map((msg, index) => {
                return (
                  <Alert
                    key={`success-${index}`}
                    kind={kinds.SUCCESS}
                  >
                    {msg}
                  </Alert>
                );
              })
            }

            {
              validationErrors.map((error, index) => {
                return (
                  <Alert
                    key={`error-${index}`}
                    kind={kinds.DANGER}
                  >
                    {error.errorMessage}
                  </Alert>
                );
              })
            }

            {
              validationWarnings.map((warning, index) => {
                return (
                  <Alert
                    key={`warning-${index}`}
                    kind={kinds.WARNING}
                  >
                    {warning.errorMessage}
                  </Alert>
                );
              })
            }
          </div> :
          null
      }

      {children}
    </div>
  );
}

Form.propTypes = {
  children: PropTypes.node.isRequired,
  validationErrors: PropTypes.arrayOf(PropTypes.object).isRequired,
  validationWarnings: PropTypes.arrayOf(PropTypes.object).isRequired,
  successMessages: PropTypes.arrayOf(PropTypes.oneOfType([PropTypes.string, PropTypes.object]))
};

Form.defaultProps = {
  validationErrors: [],
  validationWarnings: [],
  successMessages: []
};

export default Form;
