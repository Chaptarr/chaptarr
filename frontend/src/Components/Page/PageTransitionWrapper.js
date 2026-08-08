import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { withRouter } from 'react-router-dom';
import styles from './PageTransitionWrapper.css';

class PageTransitionWrapper extends Component {
  constructor(props) {
    super(props);
    this.state = {
      isTransitioning: false
    };

    this._transitionTimeoutId = null;
  }

  componentWillUnmount() {
    if (this._transitionTimeoutId) {
      clearTimeout(this._transitionTimeoutId);
      this._transitionTimeoutId = null;
    }
  }

  componentDidUpdate(prevProps) {
    const { location } = this.props;
    
    // Detect route change
    if (prevProps.location.pathname !== location.pathname) {
      // Start transition
      this.setState({ isTransitioning: true });

      if (this._transitionTimeoutId) {
        clearTimeout(this._transitionTimeoutId);
        this._transitionTimeoutId = null;
      }
      
      // Complete transition after a brief delay
      this._transitionTimeoutId = setTimeout(() => {
        this._transitionTimeoutId = null;
        this.setState({ isTransitioning: false });
      }, 50);
    }
  }

  render() {
    const { children } = this.props;
    const { isTransitioning } = this.state;
    
    return (
      <div className={isTransitioning ? styles.transitioning : styles.wrapper}>
        {children}
      </div>
    );
  }
}

PageTransitionWrapper.propTypes = {
  location: PropTypes.shape({
    pathname: PropTypes.string.isRequired
  }).isRequired,
  children: PropTypes.node.isRequired
};

export default withRouter(PageTransitionWrapper);
