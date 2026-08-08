import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import { inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ProxySettingsFields.css';

export const proxyModeOptions = [
  {
    key: 'disabled',
    value: 'Disabled',
    description: 'Chaptarr does not select a proxy. Standard HTTP_PROXY and HTTPS_PROXY environment settings still apply.'
  },
  {
    key: 'indexerOnly',
    value: 'Indexers only',
    description: 'Only indexer RSS, searches, and grabs use the proxy. Download clients, notifications, metadata, covers, and updates connect directly.'
  },
  {
    key: 'proxyEverything',
    value: 'Proxy everything',
    description: 'Classic *arr behavior. Routes Chaptarr traffic through the proxy unless it matches the proxy bypass settings.'
  }
];

const proxyTypeOptions = [
  { key: 'http', value: 'HTTP(S)' },
  { key: 'socks4', value: 'Socks4' },
  { key: 'socks5', value: 'Socks5 (Support TOR)' }
];

function hasValueProperty(field) {
  return field != null &&
    typeof field === 'object' &&
    Object.prototype.hasOwnProperty.call(field, 'value');
}

function getFieldValue(field, defaultValue) {
  if (hasValueProperty(field)) {
    return field.value ?? defaultValue;
  }

  return field ?? defaultValue;
}

function getInputProps(field, defaultValue) {
  if (hasValueProperty(field)) {
    return field;
  }

  return {
    value: field ?? defaultValue
  };
}

export function getProxyModeDescription(proxyMode) {
  return (proxyModeOptions.find((option) => option.key === proxyMode) || proxyModeOptions[0]).description;
}

function getProxyModeNotice(proxyMode) {
  if (proxyMode === 'proxyEverything') {
    return (
      <Alert kind={kinds.INFO}>
        {translate('ProxyEverythingNotice')}
      </Alert>
    );
  }

  if (proxyMode === 'indexerOnly') {
    return (
      <Alert kind={kinds.WARNING}>
        {translate('ProxyIndexerOnlyNotice')}
      </Alert>
    );
  }

  return null;
}

function ProxySettingsFields(props) {
  const {
    proxyMode,
    globalProxyId,
    proxyName,
    proxyType,
    proxyHostname,
    proxyPort,
    proxyUsername,
    proxyPassword,
    proxyBypassFilter,
    proxyBypassLocalAddresses,
    showGlobalBypassSettings,
    showProxyMode,
    showProxyName,
    showGlobalProxy,
    showProxyServerFields,
    showModeDescription,
    showTestButton,
    isTesting,
    testError,
    onInputChange,
    onTestPress
  } = props;

  const proxyModeValue = getFieldValue(proxyMode, 'disabled');
  const proxyEnabled = proxyModeValue !== 'disabled';
  const showServerFields = showProxyServerFields && (!showProxyMode || proxyEnabled);

  return (
    <>
      {showProxyMode && (
        <FormGroup>
          <FormLabel>
            {translate('ProxyMode')}
          </FormLabel>

          <div className={styles.proxyModeContent}>
            <div className={styles.radioGroup}>
              {proxyModeOptions.map((option) => (
                <label
                  key={option.key}
                  className={styles.radioOption}
                >
                  <input
                    type="radio"
                    name="proxyMode"
                    value={option.key}
                    checked={proxyModeValue === option.key}
                    onChange={() => {
                      onInputChange({ name: 'proxyMode', value: option.key });
                    }}
                    className={styles.radioInput}
                  />

                  <span className={styles.radioText}>
                    <span className={styles.radioLabel}>
                      {option.value}
                    </span>

                    <span className={styles.radioDescription}>
                      {option.description}
                    </span>
                  </span>
                </label>
              ))}
            </div>
          </div>
        </FormGroup>
      )}

      {showProxyMode && showModeDescription && (
        getProxyModeNotice(proxyModeValue)
      )}

      {proxyEnabled && showGlobalProxy && (
        <FormGroup>
          <FormLabel>
            {translate('GlobalProxy')}
          </FormLabel>

          <FormInputGroup
            type={inputTypes.PROXY_SELECT}
            name="globalProxyId"
            helpText={translate('GlobalProxyHelpText')}
            onChange={onInputChange}
            includeNone={false}
            {...getInputProps(globalProxyId, null)}
          />
        </FormGroup>
      )}

      {proxyEnabled && showGlobalBypassSettings && (
        <>
          <FormGroup>
            <FormLabel>
              {translate('BypassProxyForLocalAddresses')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.CHECK}
              name="proxyBypassLocalAddresses"
              helpText={translate('ProxyBypassLocalAddressesHelpText')}
              onChange={onInputChange}
              {...getInputProps(proxyBypassLocalAddresses, true)}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('BypassFilter')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.TEXT}
              name="proxyBypassFilter"
              helpText={translate('ProxyBypassFilterHelpText')}
              placeholder="audiobookshelf,delugevpn,*.local"
              onChange={onInputChange}
              {...getInputProps(proxyBypassFilter, '')}
            />
          </FormGroup>
        </>
      )}

      {showServerFields && showProxyName && (
        <FormGroup>
          <FormLabel>{translate('ProxyName')}</FormLabel>
          <FormInputGroup
            type={inputTypes.TEXT}
            name="proxyName"
            placeholder={translate('ProxyNamePlaceholder')}
            onChange={onInputChange}
            {...getInputProps(proxyName, '')}
          />
        </FormGroup>
      )}

      {showServerFields && (
        <>
          <FormGroup>
            <FormLabel>
              {translate('ProxyType')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.SELECT}
              name="proxyType"
              values={proxyTypeOptions}
              onChange={onInputChange}
              {...getInputProps(proxyType, 'http')}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('Hostname')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.TEXT}
              name="proxyHostname"
              placeholder="e.g. localhost or 192.168.1.1"
              onChange={onInputChange}
              {...getInputProps(proxyHostname, '')}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('Port')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.NUMBER}
              name="proxyPort"
              min={1}
              max={65535}
              onChange={onInputChange}
              {...getInputProps(proxyPort, 8080)}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('Username')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.TEXT}
              name="proxyUsername"
              helpText={translate('ProxyUsernameHelpText')}
              onChange={onInputChange}
              {...getInputProps(proxyUsername, '')}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>
              {translate('Password')}
            </FormLabel>

            <FormInputGroup
              type={inputTypes.PASSWORD}
              name="proxyPassword"
              helpText={translate('ProxyPasswordHelpText')}
              onChange={onInputChange}
              {...getInputProps(proxyPassword, '')}
            />
          </FormGroup>
        </>
      )}

      {showServerFields && showTestButton && (
        <FormGroup>
          <FormLabel>
            {translate('TestProxy')}
          </FormLabel>

          <SpinnerErrorButton
            isSpinning={isTesting}
            error={testError}
            onPress={onTestPress}
          >
            {translate('Test')}
          </SpinnerErrorButton>
        </FormGroup>
      )}
    </>
  );
}

ProxySettingsFields.propTypes = {
  proxyMode: PropTypes.oneOfType([PropTypes.string, PropTypes.object]).isRequired,
  globalProxyId: PropTypes.oneOfType([PropTypes.number, PropTypes.object]),
  proxyName: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyType: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyHostname: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyPort: PropTypes.oneOfType([PropTypes.number, PropTypes.string, PropTypes.object]),
  proxyUsername: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyPassword: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyBypassFilter: PropTypes.oneOfType([PropTypes.string, PropTypes.object]),
  proxyBypassLocalAddresses: PropTypes.oneOfType([PropTypes.bool, PropTypes.object]),
  showGlobalBypassSettings: PropTypes.bool,
  showProxyMode: PropTypes.bool,
  showProxyName: PropTypes.bool,
  showGlobalProxy: PropTypes.bool,
  showProxyServerFields: PropTypes.bool,
  showModeDescription: PropTypes.bool,
  showTestButton: PropTypes.bool,
  isTesting: PropTypes.bool,
  testError: PropTypes.object,
  onInputChange: PropTypes.func.isRequired,
  onTestPress: PropTypes.func
};

ProxySettingsFields.defaultProps = {
  showGlobalBypassSettings: false,
  showProxyMode: true,
  showProxyName: false,
  showGlobalProxy: false,
  showProxyServerFields: true,
  showModeDescription: true,
  showTestButton: false
};

export default ProxySettingsFields;
