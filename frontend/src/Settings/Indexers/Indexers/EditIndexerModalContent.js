import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import ProviderFieldFormGroup from 'Components/Form/ProviderFieldFormGroup';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import AdvancedSettingsButton from 'Settings/AdvancedSettingsButton';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';
import styles from './EditIndexerModalContent.css';

function isPrivateHost(hostname) {
  if (!hostname) {
    return false;
  }

  const host = hostname.toLowerCase();

  if (host === 'localhost') {
    return true;
  }

  // IPv4 private ranges
  if (host.startsWith('10.')) {
    return true;
  }

  if (host.startsWith('192.168.')) {
    return true;
  }

  if (host.startsWith('172.')) {
    const secondOctet = parseInt(host.split('.')[1]);
    return secondOctet >= 16 && secondOctet <= 31;
  }

  return false;
}

function looksLikeProwlarrIndexerProxy(baseUrl) {
  if (!baseUrl) {
    return false;
  }

  try {
    const url = new URL(baseUrl);
    const path = url.pathname || '';

    // Prowlarr commonly proxies Newznab/Torznab indexers at: http://host:9696/<id>/
    const hasNumericPath = (/^\/\d+(?:\/|$)/).test(path);
    const prowlarrPort = url.port === '9696';

    return hasNumericPath && (prowlarrPort || isPrivateHost(url.hostname));
  } catch {
    return false;
  }
}

function EditIndexerModalContent(props) {
  const {
    advancedSettings,
    isFetching,
    error,
    isSaving,
    isTesting,
    saveError,
    item,
    onInputChange,
    onFieldChange,
    onModalClose,
    onSavePress,
    onTestPress,
    onDeleteIndexerPress,
    onAdvancedSettingsPress,
    ...otherProps
  } = props;

  const {
    id,
    implementationName,
    name,
    enableRss,
    enableAutomaticSearch,
    enableInteractiveSearch,
    supportsRss,
    supportsSearch,
    tags,
    fields,
    priority,
    protocol,
    downloadClientId,
    proxyId
  } = item;

  const baseUrl = fields.find((field) => field.name === 'baseUrl')?.value;
  const isNewznabLike = implementationName === 'Newznab' || implementationName === 'Torznab';
  const showProxyWarning = isNewznabLike && looksLikeProwlarrIndexerProxy(baseUrl);

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {`${id ? 'Edit' : 'Add'} Indexer - ${implementationName}`}
      </ModalHeader>

      <ModalBody>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <div>
              {translate('UnableToAddANewIndexerPleaseTryAgain')}
            </div>
        }

        {
          !isFetching && !error &&
            <Form {...otherProps}>
              {
                showProxyWarning &&
                  <Alert kind={kinds.WARNING}>
                    {translate('IndexerProwlarrProxyWarning')}
                  </Alert>
              }

              <FormGroup>
                <FormLabel>
                  {translate('Name')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="name"
                  {...name}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('Protocol')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="protocolDisplay"
                  value={protocol && protocol.value ? titleCase(protocol.value) : ''}
                  readOnly={true}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={implementationName === 'MyAnonaMouse'}
              >
                <FormLabel>
                  {translate('EnableRSS')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="enableRss"
                  helpText={supportsRss.value ? translate('EnableRssHelpText') : undefined}
                  helpTextWarning={supportsRss.value ? undefined : translate('SupportsRssvalueRSSIsNotSupportedWithThisIndexer')}
                  isDisabled={!supportsRss.value}
                  {...enableRss}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={implementationName === 'MyAnonaMouse'}
              >
                <FormLabel>
                  {translate('EnableAutomaticSearch')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="enableAutomaticSearch"
                  helpText={supportsSearch.value ? translate('SupportsSearchvalueWillBeUsedWhenAutomaticSearchesArePerformedViaTheUIOrByChaptarr') : undefined}
                  helpTextWarning={supportsSearch.value ? undefined : translate('SupportsSearchvalueSearchIsNotSupportedWithThisIndexer')}
                  isDisabled={!supportsSearch.value}
                  {...enableAutomaticSearch}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={implementationName === 'MyAnonaMouse'}
              >
                <FormLabel>
                  {translate('EnableInteractiveSearch')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="enableInteractiveSearch"
                  helpText={supportsSearch.value ? translate('SupportsSearchvalueWillBeUsedWhenInteractiveSearchIsUsed') : undefined}
                  helpTextWarning={supportsSearch.value ? undefined : translate('SupportsSearchvalueSearchIsNotSupportedWithThisIndexer')}
                  isDisabled={!supportsSearch.value}
                  {...enableInteractiveSearch}
                  onChange={onInputChange}
                />
              </FormGroup>

              {
                fields.map((field) => (
                  <ProviderFieldFormGroup
                    key={field.name}
                    advancedSettings={advancedSettings}
                    provider="indexer"
                    providerData={item}
                    {...field}
                    onChange={onFieldChange}
                  />
                ))
              }

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={true}
              >
                <FormLabel>
                  {translate('IndexerPriority')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="priority"
                  helpText={translate('IndexerPriorityHelpText')}
                  min={1}
                  max={50}
                  {...priority}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={true}
              >
                <FormLabel>{translate('DownloadClient')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.DOWNLOAD_CLIENT_SELECT}
                  name="downloadClientId"
                  helpText={translate('IndexerDownloadClientHelpText')}
                  {...downloadClientId}
                  includeAny={true}
                  protocol={protocol.value}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={false}
              >
                <FormLabel>{translate('IndexerProxy')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.PROXY_SELECT}
                  name="proxyId"
                  helpText={translate('IndexerProxyHelpText')}
                  {...proxyId}
                  includeNone={true}
                  includeDirectConnection={true}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={implementationName === 'MyAnonaMouse'}
              >
                <FormLabel>
                  {translate('Tags')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.TAG}
                  name="tags"
                  helpText={translate('IndexerTagsHelpText')}
                  {...tags}
                  onChange={onInputChange}
                />
              </FormGroup>
            </Form>
        }
      </ModalBody>
      <ModalFooter>
        {
          id &&
            <Button
              className={styles.deleteButton}
              kind={kinds.DANGER}
              onPress={onDeleteIndexerPress}
            >
              {translate('Delete')}
            </Button>
        }

        <AdvancedSettingsButton
          advancedSettings={advancedSettings}
          onAdvancedSettingsPress={onAdvancedSettingsPress}
          showLabel={false}
        />

        <SpinnerErrorButton
          isSpinning={isTesting}
          error={saveError}
          onPress={onTestPress}
        >
          {translate('Test')}
        </SpinnerErrorButton>

        <Button
          onPress={onModalClose}
        >
          {translate('Cancel')}
        </Button>

        <SpinnerErrorButton
          isSpinning={isSaving}
          error={saveError}
          onPress={onSavePress}
        >
          {translate('Save')}
        </SpinnerErrorButton>
      </ModalFooter>
    </ModalContent>
  );
}

EditIndexerModalContent.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  isTesting: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onFieldChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onTestPress: PropTypes.func.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onDeleteIndexerPress: PropTypes.func
};

export default EditIndexerModalContent;
