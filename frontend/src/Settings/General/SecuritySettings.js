import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputButton from 'Components/Form/FormInputButton';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import ClipboardButton from 'Components/Link/ClipboardButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

export const authenticationMethodOptions = [
  {
    key: 'none',
    get value() {
      return translate('None');
    },
    isDisabled: true
  },
  {
    key: 'oidc',
    get value() {
      return translate('AuthOidc');
    }
  },
  {
    key: 'plex',
    get value() {
      return translate('AuthPlex');
    }
  },
  {
    key: 'external',
    get value() {
      return translate('External');
    },
    isHidden: true
  },
  {
    key: 'basic',
    get value() {
      return translate('AuthBasic');
    }
  },
  {
    key: 'forms',
    get value() {
      return translate('AuthForm');
    }
  }
];

export const authenticationRequiredOptions = [
  {
    key: 'enabled',
    get value() {
      return translate('Enabled');
    }
  },
  {
    key: 'disabledForLocalAddresses',
    get value() {
      return translate('DisabledForLocalAddresses');
    }
  }
];

const certificateValidationOptions = [
  {
    key: 'enabled',
    get value() {
      return translate('Enabled');
    }
  },
  {
    key: 'disabledForLocalAddresses',
    get value() {
      return translate('DisabledForLocalAddresses');
    }
  },
  {
    key: 'disabled',
    get value() {
      return translate('Disabled');
    }
  }
];

export function getOidcCallbackUrl() {
  const urlBase = (window.Chaptarr && window.Chaptarr.urlBase) || '';
  return `${window.location.origin}${urlBase}/signin-oidc`;
}

class SecuritySettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isConfirmApiKeyResetModalOpen: false
    };
  }

  //
  // Listeners

  onApikeyFocus = (event) => {
    event.target.select();
  };

  onResetApiKeyPress = () => {
    this.setState({ isConfirmApiKeyResetModalOpen: true });
  };

  onConfirmResetApiKey = () => {
    this.setState({ isConfirmApiKeyResetModalOpen: false });
    this.props.onConfirmResetApiKey();
  };

  onCloseResetApiKeyModal = () => {
    this.setState({ isConfirmApiKeyResetModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      settings,
      isResettingApiKey,
      onInputChange
    } = this.props;

    const {
      authenticationMethod,
      authenticationRequired,
      username,
      password,
      passwordConfirmation,
      oidcAuthority,
      oidcClientId,
      oidcClientSecret,
      oidcScopes,
      oidcAllowedEmails,
      oidcAllowedEmailDomains,
      oidcRequireEmailVerified,
      oidcAllowAnyVerifiedUser,
      apiKey,
      certificateValidation
    } = settings;

    const authenticationEnabled = authenticationMethod && authenticationMethod.value !== 'none';
    const authMethod = authenticationMethod?.value;
    const isOidc = authMethod === 'oidc';
    const oidcCallbackUrl = getOidcCallbackUrl();
    const isLocalCredentials = authMethod === 'basic' || authMethod === 'forms';
    const isSso = authMethod === 'plex' || authMethod === 'oidc';
    const hasLocalRecoveryCredentials = Boolean(username?.value);

    return (
      <FieldSet legend={translate('Security')}>
        <FormGroup>
          <FormLabel>{translate('Authentication')}</FormLabel>

          <FormInputGroup
            type={inputTypes.SELECT}
            name="authenticationMethod"
            values={authenticationMethodOptions}
            helpText={translate('AuthenticationMethodHelpText')}
            helpTextWarning={translate('AuthenticationRequiredWarning')}
            onChange={onInputChange}
            {...authenticationMethod}
          />
        </FormGroup>

        {
          authenticationEnabled ?
            <FormGroup>
              <FormLabel>{translate('AuthenticationRequired')}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="authenticationRequired"
                values={authenticationRequiredOptions}
                helpText={translate('AuthenticationRequiredHelpText')}
                onChange={onInputChange}
                {...authenticationRequired}
              />
            </FormGroup> :
            null
        }

        {
          authenticationEnabled && isSso ?
            <Alert kind={hasLocalRecoveryCredentials ? kinds.INFO : kinds.WARNING}>
              {
                hasLocalRecoveryCredentials ?
                  translate('LocalRecoveryCredentialsSetInfo', { username: username.value }) :
                  translate('LocalRecoveryCredentialsMissingWarning')
              }
            </Alert> :
            null
        }

        {
          authenticationEnabled && isLocalCredentials ?
            <FormGroup>
              <FormLabel>{translate('Username')}</FormLabel>

              <FormInputGroup
                type={inputTypes.TEXT}
                name="username"
                onChange={onInputChange}
                {...username}
              />
            </FormGroup> :
            null
        }

        {
          authenticationEnabled && isLocalCredentials ?
            <FormGroup>
              <FormLabel>{translate('Password')}</FormLabel>

              <FormInputGroup
                type={inputTypes.PASSWORD}
                name="password"
                onChange={onInputChange}
                {...password}
              />
            </FormGroup> :
            null
        }

        {
          authenticationEnabled && isLocalCredentials ?
            <FormGroup>
              <FormLabel>{translate('PasswordConfirmation')}</FormLabel>

              <FormInputGroup
                type={inputTypes.PASSWORD}
                name="passwordConfirmation"
                onChange={onInputChange}
                {...passwordConfirmation}
              />
            </FormGroup> :
            null
        }

        {
          authenticationEnabled && isSso ?
            <>
              <FormGroup>
                <FormLabel>{translate('Username')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="username"
                  helpText={translate('LocalRecoveryCredentialsHelpText')}
                  onChange={onInputChange}
                  {...username}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('Password')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="password"
                  onChange={onInputChange}
                  {...password}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('PasswordConfirmation')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="passwordConfirmation"
                  onChange={onInputChange}
                  {...passwordConfirmation}
                />
              </FormGroup>
            </> :
            null
        }

        {
          authenticationEnabled && isOidc ?
            <>
              <FormGroup>
                <FormLabel>{translate('OidcAuthority')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcAuthority"
                  helpText={translate('OidcAuthorityHelpText')}
                  onChange={onInputChange}
                  {...oidcAuthority}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcRedirectUri')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcRedirectUri"
                  readOnly={true}
                  value={oidcCallbackUrl}
                  helpText={translate('OidcRedirectUriHelpText')}
                  buttons={[
                    <ClipboardButton
                      key="copy"
                      value={oidcCallbackUrl}
                      kind={kinds.DEFAULT}
                    />
                  ]}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcClientId')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcClientId"
                  onChange={onInputChange}
                  {...oidcClientId}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcClientSecret')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="oidcClientSecret"
                  onChange={onInputChange}
                  {...oidcClientSecret}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcScopes')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcScopes"
                  helpText={translate('OidcScopesHelpText')}
                  onChange={onInputChange}
                  {...oidcScopes}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcAllowedEmails')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcAllowedEmails"
                  helpText={translate('OidcAllowedEmailsHelpText')}
                  onChange={onInputChange}
                  {...oidcAllowedEmails}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcAllowedEmailDomains')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="oidcAllowedEmailDomains"
                  helpText={translate('OidcAllowedEmailDomainsHelpText')}
                  onChange={onInputChange}
                  {...oidcAllowedEmailDomains}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcRequireEmailVerified')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="oidcRequireEmailVerified"
                  helpText={translate('OidcRequireEmailVerifiedHelpText')}
                  onChange={onInputChange}
                  {...oidcRequireEmailVerified}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('OidcAllowAnyVerifiedUser')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="oidcAllowAnyVerifiedUser"
                  helpTextWarning={translate('OidcAllowAnyVerifiedUserHelpTextWarning')}
                  onChange={onInputChange}
                  {...oidcAllowAnyVerifiedUser}
                />
              </FormGroup>
            </> :
            null
        }

        <FormGroup>
          <FormLabel>{translate('ApiKey')}</FormLabel>

          <FormInputGroup
            type={inputTypes.PASSWORD}
            name="apiKey"
            readOnly={true}
            helpTextWarning={translate('RestartRequiredHelpTextWarning')}
            buttons={[
              <ClipboardButton
                key="copy"
                value={apiKey.value}
                kind={kinds.DEFAULT}
              />,

              <FormInputButton
                key="reset"
                kind={kinds.DANGER}
                onPress={this.onResetApiKeyPress}
              >
                <Icon
                  name={icons.REFRESH}
                  isSpinning={isResettingApiKey}
                />
              </FormInputButton>
            ]}
            onChange={onInputChange}
            onFocus={this.onApikeyFocus}
            {...apiKey}
          />
        </FormGroup>

        <FormGroup>
          <FormLabel>{translate('CertificateValidation')}</FormLabel>

          <FormInputGroup
            type={inputTypes.SELECT}
            name="certificateValidation"
            values={certificateValidationOptions}
            helpText={translate('CertificateValidationHelpText')}
            onChange={onInputChange}
            {...certificateValidation}
          />
        </FormGroup>

        <ConfirmModal
          isOpen={this.state.isConfirmApiKeyResetModalOpen}
          kind={kinds.DANGER}
          title={translate('ResetAPIKey')}
          message={translate('ResetAPIKeyMessageText')}
          confirmLabel={translate('Reset')}
          onConfirm={this.onConfirmResetApiKey}
          onCancel={this.onCloseResetApiKeyModal}
        />
      </FieldSet>
    );
  }
}

SecuritySettings.propTypes = {
  settings: PropTypes.object.isRequired,
  isResettingApiKey: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onConfirmResetApiKey: PropTypes.func.isRequired
};

export default SecuritySettings;
