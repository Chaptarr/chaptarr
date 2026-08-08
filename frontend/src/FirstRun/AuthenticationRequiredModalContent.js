import PropTypes from 'prop-types';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import ClipboardButton from 'Components/Link/ClipboardButton';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import { authenticationMethodOptions, authenticationRequiredOptions, getOidcCallbackUrl } from 'Settings/General/SecuritySettings';
import translate from 'Utilities/String/translate';
import styles from './AuthenticationRequiredModalContent.css';

function onModalClose() {
  // No-op
}

function AuthenticationRequiredModalContent(props) {
  const {
    isPopulated,
    error,
    isSaving,
    settings,
    onInputChange,
    onSavePress,
    dispatchFetchStatus,
    saveError
  } = props;

  const saveErrorMessages = (() => {
    const responseJSON = saveError?.responseJSON;

    if (Array.isArray(responseJSON)) {
      return responseJSON
        .map((e) => e?.errorMessage)
        .filter(Boolean);
    }

    const message = responseJSON?.message || saveError?.responseText;
    return message ? [message] : [];
  })();

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
    oidcAllowAnyVerifiedUser
  } = settings;

  const authenticationEnabled = authenticationMethod && authenticationMethod.value !== 'none';
  const authMethod = authenticationMethod?.value;
  const isOidc = authMethod === 'oidc';
  const oidcCallbackUrl = getOidcCallbackUrl();
  const isLocalCredentials = authMethod === 'basic' || authMethod === 'forms';
  const isSso = authMethod === 'plex' || authMethod === 'oidc';
  const hasLocalRecoveryCredentials = Boolean(username?.value);

  // Track if user has manually changed the auth method
  const [userChangedAuthMethod, setUserChangedAuthMethod] = useState(false);

  const didMount = useRef(false);

  useEffect(() => {
    if (!isSaving && didMount.current) {
      dispatchFetchStatus();
    }

    didMount.current = true;
  }, [isSaving, dispatchFetchStatus]);

  // First-run default: prefer Plex unless the user has manually chosen something else.
  useEffect(() => {
    if (!userChangedAuthMethod && authenticationMethod?.value === 'none') {
      onInputChange({ name: 'authenticationMethod', value: 'plex' });
    }
  }, [authenticationMethod, userChangedAuthMethod, onInputChange]);

  // Auto-change from "none" to "basic" when username is entered
  // ONLY if user hasn't manually changed the dropdown
  const handleUsernameChange = useCallback((e) => {
    const { value } = e;

    // If username is being entered and auth is still "none" and user hasn't manually changed it
    if (value && authenticationMethod?.value === 'none' && !userChangedAuthMethod) {
      // Automatically switch to Basic authentication
      onInputChange({ name: 'authenticationMethod', value: 'basic' });
    }

    // Call the original onChange
    onInputChange(e);
  }, [authenticationMethod, userChangedAuthMethod, onInputChange]);

  // Track when user manually changes auth method
  const handleAuthMethodChange = useCallback((e) => {
    setUserChangedAuthMethod(true);
    onInputChange(e);
  }, [onInputChange]);

  return (
    <ModalContent
      showCloseButton={false}
      onModalClose={onModalClose}
    >
      <ModalHeader>
        {translate('AuthenticationRequired')}
      </ModalHeader>

      <ModalBody>
        <Alert
          className={styles.authRequiredAlert}
          kind={kinds.WARNING}
        >
          {translate('AuthenticationRequiredWarning')}
        </Alert>

        {
          saveError ?
            <Alert kind={kinds.DANGER}>
              {
                saveErrorMessages.length ?
                  saveErrorMessages.map((m, idx) => <div key={idx}>{m}</div>) :
                  translate('AuthenticationSaveError')
              }
            </Alert> :
            null
        }

        {
          isPopulated && !error ?
            <div>
              <FormGroup>
                <FormLabel>{translate('AuthenticationMethod')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="authenticationMethod"
                  values={authenticationMethodOptions}
                  helpText={translate('AuthenticationMethodHelpText')}
                  helpLink="https://discord.gg/nqFGsGUug2"
                  onChange={handleAuthMethodChange}
                  {...authenticationMethod}
                />
              </FormGroup>

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
              </FormGroup>

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
                isLocalCredentials ?
                  <>
                    <FormGroup>
                      <FormLabel>{translate('Username')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.TEXT}
                        name="username"
                        onChange={handleUsernameChange}
                        helpTextWarning={username?.value ? undefined : translate('AuthenticationRequiredUsernameHelpTextWarning')}
                        {...username}
                      />
                    </FormGroup>

                    <FormGroup>
                      <FormLabel>{translate('Password')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.PASSWORD}
                        name="password"
                        onChange={onInputChange}
                        helpTextWarning={password?.value ? undefined : translate('AuthenticationRequiredPasswordHelpTextWarning')}
                        {...password}
                      />
                    </FormGroup>

                    <FormGroup>
                      <FormLabel>{translate('PasswordConfirmation')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.PASSWORD}
                        name="passwordConfirmation"
                        onChange={onInputChange}
                        helpTextWarning={passwordConfirmation?.value ? undefined : translate('AuthenticationRequiredPasswordConfirmationHelpTextWarning')}
                        {...passwordConfirmation}
                      />
                    </FormGroup>
                  </> :
                  null
              }

              {
                isSso ?
                  <>
                    <FormGroup>
                      <FormLabel>{translate('Username')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.TEXT}
                        name="username"
                        onChange={onInputChange}
                        helpText={translate('LocalRecoveryCredentialsHelpText')}
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
                isOidc ?
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
            </div> :
            null
        }

        {
          !isPopulated && !error ? <LoadingIndicator /> : null
        }
      </ModalBody>

      <ModalFooter>
        <SpinnerButton
          kind={kinds.PRIMARY}
          isSpinning={isSaving}
          isDisabled={!authenticationEnabled}
          onPress={onSavePress}
        >
          {translate('Save')}
        </SpinnerButton>
      </ModalFooter>
    </ModalContent>
  );
}

AuthenticationRequiredModalContent.propTypes = {
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  dispatchFetchStatus: PropTypes.func.isRequired
};

export default AuthenticationRequiredModalContent;
