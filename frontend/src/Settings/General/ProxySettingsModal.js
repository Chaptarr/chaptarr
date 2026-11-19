import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Form from 'Components/Form/Form';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import { fetchGeneralSettings, fetchProxies, saveGeneralSettingsFields } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import ProxySettingsFields from './ProxySettingsFields';

function createMapStateToProps() {
  return createSelector(
    createSettingsSectionSelector('general'),
    (state) => state.settings.proxies,
    (state) => state.settings.general.item,
    (generalSettings, proxiesState, savedGeneral) => {
      const { isPopulated } = generalSettings;

      return {
        isPopulated,
        proxyMode: savedGeneral.proxyMode || 'disabled',
        globalProxyId: savedGeneral.globalProxyId ?? null,
        isProxyListPopulated: proxiesState?.isPopulated || false,
        proxyCount: proxiesState?.items?.length || 0
      };
    }
  );
}

const mapDispatchToProps = {
  fetchGeneralSettings,
  fetchProxies,
  saveGeneralSettings: saveGeneralSettingsFields
};

class ProxySettingsModal extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = this.getStateForSelectedProxy(props.selectedProxy);
  }

  componentDidMount() {
    if (this.props.isOpen) {
      this.onOpen();
    }
  }

  componentDidUpdate(prevProps) {
    if (!prevProps.isOpen && this.props.isOpen) {
      this.onOpen();
    }

    if (prevProps.isOpen && !this.props.isOpen) {
      this.resetLocalState();
    }

    if (this.props.isOpen && this.props.selectedProxy !== prevProps.selectedProxy) {
      this.resetLocalState(this.props.selectedProxy);
    }
  }

  componentWillUnmount() {
    this.saveRequest = null;
    this.testRequest = null;
  }

  onOpen = () => {
    this.props.fetchGeneralSettings();
    this.props.fetchProxies();
    this.resetLocalState(this.props.selectedProxy);
  };

  getStateForSelectedProxy = (selectedProxy) => {
    const proxyId = selectedProxy?.id ?? null;

    return {
      proxyId,
      proxyName: selectedProxy?.name ?? '',
      proxyType: selectedProxy?.type ?? 'http',
      proxyHostname: selectedProxy?.hostname ?? '',
      proxyPort: selectedProxy?.port ?? 8080,
      proxyUsername: selectedProxy?.username ?? '',
      proxyPassword: selectedProxy?.password ?? '',
      isTestingProxy: false,
      testError: null,
      hasTestedSuccessfully: false,
      testedConfig: null,
      isSavingProxy: false,
      proxySaveError: null
    };
  };

  resetLocalState = (selectedProxy = null) => {
    this.saveRequest = null;
    this.testRequest = null;
    this.setState(this.getStateForSelectedProxy(selectedProxy));
  };

  resetTestState = () => {
    this.setState({
      isTestingProxy: false,
      testError: null,
      hasTestedSuccessfully: false,
      testedConfig: null,
      proxySaveError: null
    });
  };

  isConfigUnchanged = () => {
    const { testedConfig } = this.state;

    if (!testedConfig) {
      return false;
    }

    return testedConfig.proxyName === this.state.proxyName &&
      testedConfig.proxyType === this.state.proxyType &&
      testedConfig.proxyHostname === this.state.proxyHostname &&
      testedConfig.proxyPort === this.state.proxyPort &&
      testedConfig.proxyUsername === this.state.proxyUsername &&
      testedConfig.proxyPassword === this.state.proxyPassword;
  };

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value }, this.resetTestState);
  };

  onSavePress = () => {
    this.saveProxyThenGeneral();
  };

  saveProxyThenGeneral = () => {
    const {
      proxyId,
      proxyName,
      proxyType,
      proxyHostname,
      proxyPort,
      proxyUsername,
      proxyPassword
    } = this.state;

    if (!proxyHostname || !proxyPort) {
      this.setState({
        proxySaveError: {
          status: 400,
          responseJSON: [
            { message: translate('HostnameAndPortAreRequired'), isWarning: false }
          ]
        }
      });
      return;
    }

    const { proxyCount, proxyMode, globalProxyId } = this.props;
    const payload = {
      name: proxyName || 'Default Proxy',
      type: proxyType,
      hostname: proxyHostname,
      port: proxyPort,
      username: proxyUsername,
      password: proxyPassword
    };

    this.setState({ isSavingProxy: true, proxySaveError: null });
    const request = createAjaxRequest({
      url: proxyId ? `/settings/proxy/${proxyId}` : '/settings/proxy',
      method: proxyId ? 'PUT' : 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(payload)
    }).request.then((savedProxy) => {
      const savedId = savedProxy?.id ?? proxyId;
      const isFirstProxy = !proxyId && proxyCount === 0;
      const isProxyRoutingDisabled = proxyMode === 'disabled';
      const shouldRepairDefaultProxy = savedId != null && (isFirstProxy || isProxyRoutingDisabled || !globalProxyId);

      this.props.fetchProxies();

      if (shouldRepairDefaultProxy) {
        const updates = { globalProxyId: savedId };
        if (isProxyRoutingDisabled) {
          updates.proxyMode = 'proxyEverything';
        }

        if (this.saveRequest === request) {
          this.setState({ proxyId: savedId });
        }
        return this.props.saveGeneralSettings(updates);
      }
    });
    this.saveRequest = request;
    request.done(() => {
      if (this.saveRequest === request) {
        this.setState({ isSavingProxy: false });
        this.props.onModalClose();
      }
    });
    request.fail((xhr) => {
      if (this.saveRequest === request) {
        this.setState({ isSavingProxy: false, proxySaveError: xhr });
      }
    });
  };

  onTestProxyPress = () => {
    const {
      proxyName,
      proxyType,
      proxyHostname,
      proxyPort,
      proxyUsername,
      proxyPassword
    } = this.state;

    if (!proxyHostname || !proxyPort) {
      this.setState({
        testError: {
          status: 400,
          responseJSON: [
            { message: translate('HostnameAndPortAreRequired'), isWarning: false }
          ]
        }
      });
      return;
    }

    this.setState({ isTestingProxy: true, testError: null });

    const request = createAjaxRequest({
      url: '/settings/proxy/test',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        name: proxyName || 'Proxy',
        type: proxyType,
        hostname: proxyHostname,
        port: proxyPort,
        username: proxyUsername,
        password: proxyPassword
      })
    });

    this.testRequest = request;
    request.request.done((data) => {
      if (this.testRequest !== request) {
        return;
      }
      if (data?.isValid) {
        this.setState({
          isTestingProxy: false,
          testError: null,
          hasTestedSuccessfully: true,
          testedConfig: {
            proxyName,
            proxyType,
            proxyHostname,
            proxyPort,
            proxyUsername,
            proxyPassword
          }
        });

        return;
      }

      this.setState({
        isTestingProxy: false,
        testError: {
          status: 400,
          responseJSON: [
            { message: data?.message || translate('ProxyTestFailed'), isWarning: false }
          ]
        }
      });
    });

    request.request.fail((xhr) => {
      if (this.testRequest !== request) {
        return;
      }
      this.setState({
        isTestingProxy: false,
        testError: xhr
      });
    });
  };

  render() {
    const {
      isOpen,
      onModalClose,
      proxyMode,
      isPopulated,
      isProxyListPopulated
    } = this.props;

    const {
      proxyName,
      proxyType,
      proxyHostname,
      proxyPort,
      proxyUsername,
      proxyPassword,
      isTestingProxy,
      testError,
      isSavingProxy,
      proxySaveError,
      hasTestedSuccessfully
    } = this.state;

    if (!isPopulated || !isProxyListPopulated) {
      return (
        <Modal
          isOpen={isOpen}
          onModalClose={onModalClose}
        >
          <ModalContent onModalClose={onModalClose}>
            <ModalHeader>
              {this.props.selectedProxy ? translate('ConfigureProxy', { 0: this.props.selectedProxy.name }) : translate('AddProxy')}
            </ModalHeader>

            <ModalBody>
              <LoadingIndicator />
            </ModalBody>
          </ModalContent>
        </Modal>
      );
    }

    const canSaveNow = hasTestedSuccessfully && this.isConfigUnchanged();
    const primaryIsSave = canSaveNow;
    const primaryIsSpinning = primaryIsSave ? isSavingProxy : isTestingProxy;
    const primaryError = primaryIsSave ? proxySaveError : testError;

    return (
      <Modal
        isOpen={isOpen}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>
            {this.props.selectedProxy ? `Configure Proxy - ${this.props.selectedProxy.name}` : 'Add Proxy'}
          </ModalHeader>

          <ModalBody>
            <Form>
              <ProxySettingsFields
                proxyMode={proxyMode}
                proxyName={proxyName}
                proxyType={proxyType}
                proxyHostname={proxyHostname}
                proxyPort={proxyPort}
                proxyUsername={proxyUsername}
                proxyPassword={proxyPassword}
                showProxyMode={false}
                showProxyName={true}
                showProxyServerFields={true}
                onInputChange={this.onInputChange}
              />
            </Form>
          </ModalBody>

          <ModalFooter>
            <Button onPress={onModalClose}>{translate('Cancel')}</Button>

            <SpinnerErrorButton
              kind={primaryIsSave ? kinds.PRIMARY : kinds.DEFAULT}
              isSpinning={primaryIsSpinning}
              isDisabled={isSavingProxy}
              error={primaryError}
              onPress={primaryIsSave ? this.onSavePress : this.onTestProxyPress}
            >
              {primaryIsSave ? translate('Save') : translate('Test')}
            </SpinnerErrorButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

ProxySettingsModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  selectedProxy: PropTypes.object,
  proxyMode: PropTypes.string.isRequired,
  globalProxyId: PropTypes.number,
  isProxyListPopulated: PropTypes.bool.isRequired,
  proxyCount: PropTypes.number.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  fetchGeneralSettings: PropTypes.func.isRequired,
  fetchProxies: PropTypes.func.isRequired,
  saveGeneralSettings: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ProxySettingsModal);
