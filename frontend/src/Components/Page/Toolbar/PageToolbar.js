import PropTypes from 'prop-types';
import React, { Component } from 'react';
import styles from './PageToolbar.css';

class PageToolbar extends Component {

  //
  // Render

  render() {
    const {
      className,
      controls,
      children
    } = this.props;

    return (
      <div className={className}>
        {
          controls &&
            <div className={styles.controls}>
              {controls}
            </div>
        }
        <div className={styles.actions}>
          {children}
        </div>
      </div>
    );
  }
}

PageToolbar.propTypes = {
  className: PropTypes.string,
  controls: PropTypes.node,
  children: PropTypes.node.isRequired
};

PageToolbar.defaultProps = {
  className: styles.toolbar
};

export default PageToolbar;
