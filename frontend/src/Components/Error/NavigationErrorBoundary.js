import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { withRouter } from 'react-router-dom';
import ErrorBoundaryError from './ErrorBoundaryError';

class NavigationErrorBoundary extends Component {
  constructor(props) {
    super(props);
    this.state = { 
      hasError: false,
      error: null,
      info: null
    };
  }

  static getDerivedStateFromError(error) {
    return { hasError: true };
  }

  componentDidCatch(error, info) {
    console.error('Navigation Error:', error, info);
    this.setState({
      error,
      info
    });
  }

  componentDidUpdate(prevProps) {
    if (prevProps.location.pathname !== this.props.location.pathname && this.state.hasError) {
      this.setState({ 
        hasError: false,
        error: null,
        info: null
      });
    }
  }

  render() {
    if (this.state.hasError) {
      return (
        <ErrorBoundaryError
          className={this.props.errorClassName}
          messageClassName={this.props.messageClassName}
          detailsClassName={this.props.detailsClassName}
          message={this.props.errorMessage}
          error={this.state.error}
          info={this.state.info}
        />
      );
    }

    return this.props.children;
  }
}

NavigationErrorBoundary.propTypes = {
  children: PropTypes.node.isRequired,
  errorClassName: PropTypes.string,
  messageClassName: PropTypes.string,
  detailsClassName: PropTypes.string,
  errorMessage: PropTypes.string,
  location: PropTypes.shape({
    pathname: PropTypes.string.isRequired
  }).isRequired
};

NavigationErrorBoundary.defaultProps = {
  errorMessage: 'Oops......well this is awkward'
};

export default withRouter(NavigationErrorBoundary);