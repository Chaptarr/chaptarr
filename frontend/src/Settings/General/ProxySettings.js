import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons } from 'Helpers/Props';
import { fetchProxies } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import ProxySettingsFields from './ProxySettingsFields';
import ProxySettingsModal from './ProxySettingsModal';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.proxies,
    (proxiesState) => {
      return {
        proxies: proxiesState?.items || []
      };
    }
  );
}

const mapDispatchToProps = {
  fetchProxies
};

class ProxySettings extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      isProxyModalOpen: false,
      selectedProxy: null
    };
  }

  componentDidMount() {
    this.props.fetchProxies();
  }

  onAddProxyPress = () => {
    this.setState({
      isProxyModalOpen: true,
      selectedProxy: null
    });
  };

  onConfigureProxyPress = (proxy) => {
    this.setState({
      isProxyModalOpen: true,
      selectedProxy: proxy
    });
  };

  onModalClose = () => {
    this.setState({
      isProxyModalOpen: false,
      selectedProxy: null
    });

    this.props.fetchProxies();
  };

  render() {
    const {
      settings,
      proxies,
      onInputChange,
      isTesting,
      testError,
      onTestPress
    } = this.props;

    const { proxyMode, globalProxyId } = settings;
    const proxyEnabled = proxyMode && proxyMode.value !== 'disabled';
    const hasProxies = proxies.length > 0;
    const selectedGlobalProxyId = globalProxyId && globalProxyId.value;
    const hasSelectedGlobalProxy = selectedGlobalProxyId &&
      proxies.some((proxy) => proxy.id === selectedGlobalProxyId);
    const proxyConfigured = hasProxies && proxyEnabled && hasSelectedGlobalProxy;

    const legend = (
      <span>
        {translate('ProxySettings')}
        {proxyConfigured && (
          <Icon
            name={icons.CHECK}
            className="fa-fw"
            style={{ marginLeft: '5px', color: '#27c24c' }}
          />
        )}
      </span>
    );

    return (
      <FieldSet legend={legend}>
        {
          !hasProxies &&
            <p>
              {translate('NoProxiesConfigured')}
            </p>
        }

        {
          hasProxies &&
            <ProxySettingsFields
              proxyMode={settings.proxyMode}
              globalProxyId={settings.globalProxyId}
              proxyType={settings.proxyType}
              proxyHostname={settings.proxyHostname}
              proxyPort={settings.proxyPort}
              proxyUsername={settings.proxyUsername}
              proxyPassword={settings.proxyPassword}
              proxyBypassFilter={settings.proxyBypassFilter}
              proxyBypassLocalAddresses={settings.proxyBypassLocalAddresses}
              showGlobalBypassSettings={true}
              showGlobalProxy={true}
              showProxyServerFields={false}
              showTestButton={false}
              isTesting={isTesting}
              testError={testError}
              onInputChange={onInputChange}
              onTestPress={onTestPress}
            />
        }

        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '8px' }}>
          <Button
            onPress={this.onAddProxyPress}
          >
            {translate('AddProxy')}
          </Button>

          {
            proxies.map((proxy) => (
              <Button
                key={proxy.id}
                onPress={() => this.onConfigureProxyPress(proxy)}
              >
                {translate('ConfigureProxy', [proxy.name])}
              </Button>
            ))
          }
        </div>

        <ProxySettingsModal
          isOpen={this.state.isProxyModalOpen}
          onModalClose={this.onModalClose}
          selectedProxy={this.state.selectedProxy}
        />
      </FieldSet>
    );
  }
}

ProxySettings.propTypes = {
  settings: PropTypes.object.isRequired,
  proxies: PropTypes.arrayOf(PropTypes.object).isRequired,
  onInputChange: PropTypes.func.isRequired,
  isTesting: PropTypes.bool,
  testError: PropTypes.object,
  onTestPress: PropTypes.func,
  fetchProxies: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ProxySettings);
