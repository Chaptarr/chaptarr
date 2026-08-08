import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import ProxySettingsModal from 'Settings/General/ProxySettingsModal';
import { fetchGeneralSettings, fetchProxies } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import styles from './Quickstart.css';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.general,
    (state) => state.settings.proxies,
    (generalSettings, proxiesState) => {
      const proxies = proxiesState?.items || [];

      return {
        proxies
      };
    }
  );
}

class QuickstartProxySection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isProxyModalOpen: false,
      selectedProxy: null
    };
  }

  componentDidMount() {
    // Ensure proxies list is populated for display
    if (this.props.fetchProxies) {
      this.props.fetchProxies();
    }
  }

  //
  // Listeners

  onAddProxyPress = () => {
    this.setState({ isProxyModalOpen: true, selectedProxy: null });
  };

  onConfigureProxyPress = (proxy) => {
    this.setState({ isProxyModalOpen: true, selectedProxy: proxy });
  };

  onModalClose = () => {
    this.setState({ isProxyModalOpen: false, selectedProxy: null });

    if (this.props.fetchGeneralSettings) {
      this.props.fetchGeneralSettings();
    }

    if (this.props.fetchProxies) {
      this.props.fetchProxies();
    }
  };

  //
  // Render

  render() {
    const { isProxyModalOpen, selectedProxy } = this.state;
    const { proxies } = this.props;

    // Summary line removed from UI to reduce clutter

    return (
      <>
        <div className={styles.quickstartCardActions}>
          <button
            className={styles.quickstartCardButton}
            onClick={this.onAddProxyPress}
          >
            {translate('AddProxy')}
          </button>

          {proxies && proxies.map((p) => (
            <button
              key={p.id}
              className={styles.quickstartCardButton}
              onClick={() => this.onConfigureProxyPress(p)}
            >
              {`Configure ${p.name}`}
            </button>
          ))}
        </div>

        <ProxySettingsModal
          isOpen={isProxyModalOpen}
          onModalClose={this.onModalClose}
          selectedProxy={selectedProxy}
        />
      </>
    );
  }
}

const mapDispatchToProps = {
  fetchGeneralSettings,
  fetchProxies
};

QuickstartProxySection.propTypes = {
  proxies: PropTypes.array,
  fetchGeneralSettings: PropTypes.func.isRequired,
  fetchProxies: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartProxySection);
