import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds } from 'Helpers/Props';
import { numberSettingShape, stringSettingShape } from 'Helpers/Props/Shapes/settingShape';
import AdvancedSettingsButton from 'Settings/AdvancedSettingsButton';
import translate from 'Utilities/String/translate';
import styles from './EditRemotePathMappingModalContent.css';

function PathSuggestions(props) {
  const {
    name,
    suggestions,
    onInputChange
  } = props;

  if (!suggestions.length) {
    return null;
  }

  return (
    <div className={styles.pathSuggestions}>
      {
        suggestions.map((suggestion) => {
          return (
            <button
              key={suggestion}
              type="button"
              className={styles.pathSuggestion}
              title={suggestion}
              onClick={() => onInputChange({ name, value: suggestion })}
            >
              {suggestion}
            </button>
          );
        })
      }
    </div>
  );
}

function getTestResultMessage(testResult) {
  if (!testResult.isMapped) {
    return `Test result: ${testResult.remotePath} and ${testResult.mappedPath} resolve to the same path, so this mapping does not change anything.`;
  }

  if (testResult.downloadClientPathChecked && !testResult.downloadClientPathMatched) {
    return `Test result: Chaptarr can map ${testResult.remotePath} to ${testResult.mappedPath}, but the selected download client did not report a matching path. Check the "Download client sees" value.`;
  }

  if (testResult.downloadClientItemPathChecked && !testResult.downloadClientItemPathExists) {
    return `Test result: A current download-client item maps to ${testResult.downloadClientItemMappedPath}, but Chaptarr cannot see that actual item there. This mapping is probably wrong.`;
  }

  if (testResult.downloadClientItemPathChecked && !testResult.downloadClientItemPathWritable) {
    return `Test result: Chaptarr can see a current download-client item at ${testResult.downloadClientItemMappedPath}, but cannot write there.`;
  }

  if (!testResult.mappedPathExists) {
    return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. Chaptarr cannot see that mapped path.`;
  }

  if (!testResult.mappedPathWritable) {
    return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. Chaptarr can see it, but cannot write there.`;
  }

  if (testResult.downloadClientId === 0) {
    return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. Chaptarr can see and write there. This host-wide mapping is not tied to one download client, so no client comparison was performed.`;
  }

  if (testResult.downloadClientItemPathChecked) {
    return `Test result: Chaptarr can see a current download-client item at ${testResult.downloadClientItemMappedPath}.`;
  }

  if (testResult.downloadClientTestError) {
    return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. ${testResult.downloadClientTestError}`;
  }

  if (testResult.downloadClientPathChecked) {
    return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. Chaptarr can see and write there, but no current download item was available to prove both sides are the same folder.`;
  }

  return `Test result: ${testResult.remotePath} maps to ${testResult.mappedPath}. Chaptarr can see and write there, but no download client was available to compare against.`;
}

function getTestResultKind(testResult) {
  if (testResult.downloadClientId === 0) {
    if (
      testResult.isMapped &&
      testResult.localPathExists &&
      testResult.localPathWritable &&
      testResult.mappedPathExists &&
      testResult.mappedPathWritable
    ) {
      return kinds.SUCCESS;
    }

    return kinds.WARNING;
  }

  if (
    testResult.downloadClientPathMatched &&
    testResult.downloadClientItemPathChecked &&
    testResult.downloadClientItemPathExists &&
    testResult.downloadClientItemPathWritable
  ) {
    return kinds.SUCCESS;
  }

  return kinds.WARNING;
}

function EditRemotePathMappingModalContent(props) {
  const {
    id,
    isFetching,
    error,
    isSaving,
    saveError,
    isTesting,
    testError,
    testResult,
    showAdvancedScope,
    item,
    downloadClientHosts,
    downloadClientOptions,
    downloadClientPathSuggestions,
    chaptarrPathSuggestions,
    onInputChange,
    onAdvancedScopePress,
    onTestPress,
    onSavePress,
    onModalClose,
    onDeleteRemotePathMappingPress,
    ...otherProps
  } = props;

  const {
    downloadClientId,
    host,
    remotePath,
    localPath
  } = item;
  const isDownloadClientScoped = downloadClientId.value > 0;
  const showDownloadClientScope = showAdvancedScope || isDownloadClientScoped;

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {id ? 'Edit Remote Path Mapping' : 'Add Remote Path Mapping'}
      </ModalHeader>

      <ModalBody className={styles.body}>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && !!error &&
            <div>
              {translate('UnableToAddANewRemotePathMappingPleaseTryAgain')}
            </div>
        }

        {
          !isFetching && !error &&
            <Form {...otherProps}>
              {
                isDownloadClientScoped &&
                  <Alert kind={kinds.WARNING}>
                    {translate('RemotePathMappingScopeWarning')}
                  </Alert>
              }

              <FormGroup
                advancedSettings={showDownloadClientScope}
                isAdvanced={true}
              >
                <FormLabel>
                  {translate('DownloadClient')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="downloadClientId"
                  helpText={translate('RemotePathMappingHostWideHelpText')}
                  {...downloadClientId}
                  values={downloadClientOptions}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('Host')}
                </FormLabel>

                {
                  isDownloadClientScoped ?
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="host"
                      helpText={translate('RemotePathMappingDerivedHostHelpText')}
                      {...host}
                      isDisabled={true}
                      onChange={onInputChange}
                    /> :
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="host"
                      helpText={translate('SettingsRemotePathMappingHostHelpText')}
                      {...host}
                      values={downloadClientHosts}
                      onChange={onInputChange}
                    />
                }
              </FormGroup>

              <div className={styles.mappingSection}>
                <div className={styles.mappingTitle}>
                  {translate('RemotePathMappingTitle')}
                </div>

                <div className={styles.mappingHelp}>
                  {translate('RemotePathMappingHelpText')}
                </div>

                <div className={styles.mappingGridHeader}>
                  <div>{translate('RemotePathMappingDownloadClientSees')}</div>
                  <div />
                  <div>{translate('RemotePathMappingChaptarrSees')}</div>
                </div>

                <div className={styles.mappingRow}>
                  <div className={styles.mappingSide}>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="remotePath"
                      helpText={translate('SettingsRemotePathMappingRemotePathHelpText')}
                      {...remotePath}
                      onChange={onInputChange}
                    />

                    <PathSuggestions
                      name="remotePath"
                      suggestions={downloadClientPathSuggestions}
                      onInputChange={onInputChange}
                    />
                  </div>

                  <div className={styles.mappingArrow}>
                    {'→'}
                  </div>

                  <div className={styles.mappingSide}>
                    <FormInputGroup
                      type={inputTypes.PATH}
                      name="localPath"
                      helpText={translate('SettingsRemotePathMappingLocalPathHelpText')}
                      {...localPath}
                      onChange={onInputChange}
                    />

                    <PathSuggestions
                      name="localPath"
                      suggestions={chaptarrPathSuggestions}
                      onInputChange={onInputChange}
                    />
                  </div>
                </div>
              </div>

              {
                testResult &&
                  <Alert kind={getTestResultKind(testResult)}>
                    {getTestResultMessage(testResult)}
                  </Alert>
              }
            </Form>
        }
      </ModalBody>

      <ModalFooter>
        {
          id &&
            <Button
              className={styles.deleteButton}
              kind={kinds.DANGER}
              onPress={onDeleteRemotePathMappingPress}
            >
              {translate('Delete')}
            </Button>
        }

        <Button
          onPress={onModalClose}
        >
          {translate('Cancel')}
        </Button>

        <AdvancedSettingsButton
          advancedSettings={showDownloadClientScope}
          onAdvancedSettingsPress={onAdvancedScopePress}
          showLabel={false}
        />

        <SpinnerErrorButton
          isSpinning={isTesting}
          error={testError}
          onPress={onTestPress}
        >
          {translate('TestMapping')}
        </SpinnerErrorButton>

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

const remotePathMappingShape = {
  downloadClientId: PropTypes.shape(numberSettingShape).isRequired,
  host: PropTypes.shape(stringSettingShape).isRequired,
  remotePath: PropTypes.shape(stringSettingShape).isRequired,
  localPath: PropTypes.shape(stringSettingShape).isRequired
};

EditRemotePathMappingModalContent.propTypes = {
  id: PropTypes.number,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isTesting: PropTypes.bool.isRequired,
  testError: PropTypes.object,
  testResult: PropTypes.object,
  showAdvancedScope: PropTypes.bool.isRequired,
  item: PropTypes.shape(remotePathMappingShape).isRequired,
  downloadClientHosts: PropTypes.arrayOf(PropTypes.object).isRequired,
  downloadClientOptions: PropTypes.arrayOf(PropTypes.object).isRequired,
  downloadClientPathSuggestions: PropTypes.arrayOf(PropTypes.string).isRequired,
  chaptarrPathSuggestions: PropTypes.arrayOf(PropTypes.string).isRequired,
  onInputChange: PropTypes.func.isRequired,
  onAdvancedScopePress: PropTypes.func.isRequired,
  onTestPress: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onDeleteRemotePathMappingPress: PropTypes.func
};

PathSuggestions.propTypes = {
  name: PropTypes.string.isRequired,
  suggestions: PropTypes.arrayOf(PropTypes.string).isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default EditRemotePathMappingModalContent;
