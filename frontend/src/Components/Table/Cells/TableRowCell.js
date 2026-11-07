import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BidiText from 'Components/BidiText';
import styles from './TableRowCell.css';

class TableRowCell extends Component {

  //
  // Render

  render() {
    const {
      className,
      children,
      ...otherProps
    } = this.props;

    const content = typeof children === 'string' || typeof children === 'number' ?
      <BidiText>{children}</BidiText> :
      children;

    return (
      <td
        className={className}
        {...otherProps}
      >
        {content}
      </td>
    );
  }
}

TableRowCell.propTypes = {
  className: PropTypes.string.isRequired,
  children: PropTypes.oneOfType([PropTypes.string, PropTypes.node])
};

TableRowCell.defaultProps = {
  className: styles.cell
};

export default TableRowCell;
