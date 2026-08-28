import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import VirtualTableRowCell from 'Components/Table/Cells/TableRowCell';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './BookStatusCell.css';

function BookStatusCell(props) {
  const {
    className,
    grabbed,
    monitored,
    component: Component,
    ...otherProps
  } = props;

  let iconName = icons.UNMONITORED;
  let iconTitle = translate('Unmonitored');

  if (monitored) {
    iconName = icons.MONITORED;
    iconTitle = translate('Monitored');
  }

  if (grabbed) {
    iconName = icons.DOWNLOADING;
    iconTitle = translate('BookIsDownloading');
  }

  return (
    <Component
      className={className}
      {...otherProps}
    >
      <Icon
        className={styles.statusIcon}
        name={iconName}
        title={iconTitle}
      />
    </Component>
  );
}

BookStatusCell.propTypes = {
  className: PropTypes.string.isRequired,
  grabbed: PropTypes.bool,
  monitored: PropTypes.bool,
  component: PropTypes.elementType
};

BookStatusCell.defaultProps = {
  className: styles.status,
  component: VirtualTableRowCell
};

export default BookStatusCell;
