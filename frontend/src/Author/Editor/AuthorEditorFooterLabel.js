import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import SpinnerIcon from 'Components/SpinnerIcon';
import Popover from 'Components/Tooltip/Popover';
import { icons, tooltipPositions } from 'Helpers/Props';
import styles from './AuthorEditorFooterLabel.css';

function AuthorEditorFooterLabel(props) {
  const {
    className,
    label,
    isSaving,
    popoverBody,
    popoverTitle
  } = props;

  return (
    <div className={className}>
      {label}

      {
        popoverBody ?
          <Popover
            anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
            title={popoverTitle || label}
            body={popoverBody}
            position={tooltipPositions.RIGHT}
          /> :
          null
      }

      {
        isSaving &&
          <SpinnerIcon
            className={styles.savingIcon}
            name={icons.SPINNER}
            isSpinning={true}
          />
      }
    </div>
  );
}

AuthorEditorFooterLabel.propTypes = {
  className: PropTypes.string.isRequired,
  label: PropTypes.string.isRequired,
  isSaving: PropTypes.bool.isRequired,
  popoverBody: PropTypes.node,
  popoverTitle: PropTypes.string
};

AuthorEditorFooterLabel.defaultProps = {
  className: styles.label
};

export default AuthorEditorFooterLabel;
