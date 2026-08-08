import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { forceCloseAllModals } from 'Utilities/modalCleanup';
import translate from 'Utilities/String/translate';

class AppErrorBoundary extends Component {
  constructor(props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error) {
    // Update state so the next render will show the fallback UI
    return { hasError: true, error };
  }

  componentDidCatch(error, errorInfo) {
    console.error('App crashed with error:', error);
    console.error('Error info:', errorInfo);

    // Clean up any stuck modals that might have caused the crash
    try {
      forceCloseAllModals();
      console.log('Cleaned up modals after app crash');
    } catch (cleanupError) {
      console.error('Error during modal cleanup:', cleanupError);
    }

    // Send error to monitoring service
    if (window.Sentry && errorInfo && errorInfo.componentStack) {
      window.Sentry.captureException(error, {
        contexts: {
          react: {
            componentStack: errorInfo.componentStack
          }
        }
      });
    }
  }

  handleRetry = () => {
    // Clear error state and try to recover
    this.setState({ hasError: false, error: null });

    // Force a page reload as last resort
    setTimeout(() => {
      if (this.state.hasError) {
        window.location.reload();
      }
    }, 100);
  };

  render() {
    if (this.state.hasError) {
      return (
        <div style={{
          padding: '20px',
          textAlign: 'center',
          fontFamily: 'sans-serif',
          backgroundColor: '#f5f5f5',
          minHeight: '100vh',
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'center',
          alignItems: 'center'
        }}
        >
          <h1 style={{ color: '#d32f2f', marginBottom: '20px' }}>
            {translate('AppErrorBoundaryTitle')}
          </h1>
          <p style={{ marginBottom: '20px', maxWidth: '600px' }}>
            {translate('AppErrorBoundaryDescription')}
          </p>
          <div style={{ marginBottom: '20px' }}>
            <button
              onClick={this.handleRetry}
              style={{
                padding: '10px 20px',
                backgroundColor: '#1976d2',
                color: 'white',
                border: 'none',
                borderRadius: '4px',
                cursor: 'pointer',
                marginRight: '10px'
              }}
            >
              {translate('AppErrorBoundaryTryAgain')}
            </button>
            <button
              onClick={() => window.location.reload()}
              style={{
                padding: '10px 20px',
                backgroundColor: '#757575',
                color: 'white',
                border: 'none',
                borderRadius: '4px',
                cursor: 'pointer'
              }}
            >
              {translate('AppErrorBoundaryReloadPage')}
            </button>
          </div>
          {this.state.error && (
            <details style={{ marginTop: '20px', textAlign: 'left', maxWidth: '800px' }}>
              <summary style={{ cursor: 'pointer', marginBottom: '10px' }}>
                {translate('AppErrorBoundaryTechnicalDetails')}
              </summary>
              <pre style={{
                backgroundColor: '#f0f0f0',
                padding: '10px',
                borderRadius: '4px',
                overflow: 'auto',
                fontSize: '12px'
              }}
              >
                {this.state.error.toString()}
              </pre>
            </details>
          )}
        </div>
      );
    }

    return this.props.children;
  }
}

AppErrorBoundary.propTypes = {
  children: PropTypes.node.isRequired
};

export default AppErrorBoundary;
