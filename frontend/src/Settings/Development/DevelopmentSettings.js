import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './DevelopmentSettings.css';

const logLevelOptions = [
  { key: 'info', value: 'Info' },
  { key: 'debug', value: 'Debug' },
  { key: 'trace', value: 'Trace' }
];

class DevelopmentSettings extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isResetConfirmModalOpen: false,
      isResetConfirmFinalModalOpen: false,
      isResetting: false,
      resetError: null
    };

    this.preResetStartTime = null;
  }

  //
  // Listeners

  onResetEverythingPress = () => {
    this.setState({
      isResetConfirmModalOpen: true,
      isResetConfirmFinalModalOpen: false,
      resetError: null
    });
  };

  onResetConfirmModalClose = () => {
    this.setState({ isResetConfirmModalOpen: false });
  };

  onResetConfirmFinalModalClose = () => {
    this.setState({ isResetConfirmFinalModalOpen: false });
  };

  onResetConfirmContinue = () => {
    this.setState({
      isResetConfirmModalOpen: false,
      isResetConfirmFinalModalOpen: true
    });
  };

  onResetConfirmFinal = () => {
    this.setState({
      isResetConfirmFinalModalOpen: false,
      isResetting: true,
      resetError: null
    }, () => {
      // Capture the current process start time first so the restart poll can tell the dying
      // pre-reset process (same start time, wiped schema) apart from the restarted one.
      const { request: statusRequest } = createAjaxRequest({
        url: '/system/status',
        method: 'GET',
        dataType: 'json'
      });

      const startReset = () => {
        const { request } = createAjaxRequest({
          url: '/system/reset',
          method: 'POST',
          dataType: 'json'
        });

        request.done(() => {
          this.waitForRestartAndReload();
        });

        request.fail((xhr) => {
          this.setState({
            isResetting: false,
            resetError: xhr
          });
        });
      };

      statusRequest.done((data) => {
        this.preResetStartTime = (data && data.startTime) || null;
        startReset();
      });

      statusRequest.fail(() => {
        this.preResetStartTime = null;
        startReset();
      });
    });
  };

  waitForRestartAndReload = () => {
    const urlBase = (window.Chaptarr && window.Chaptarr.urlBase) || '';

    const attempt = () => {
      const { request } = createAjaxRequest({
        url: '/system/status',
        method: 'GET',
        dataType: 'json'
      });

      request.done((data) => {
        // A 200 can still come from the dying pre-reset process, which keeps serving its wiped
        // schema until the restart completes. Only reload once the reported start time differs
        // from the pre-reset baseline; without a baseline, rely on the 401/503 signals instead.
        const startTime = data && data.startTime;

        if (!this.preResetStartTime || (startTime && startTime !== this.preResetStartTime)) {
          window.location = `${urlBase}/`;
          return;
        }

        setTimeout(attempt, 2000);
      });

      request.fail((xhr) => {
        // After reset the API key changes; a 401 means we're back online and should reload to fetch the new key.
        if (xhr && xhr.status === 401) {
          window.location = `${urlBase}/`;
          return;
        }

        setTimeout(attempt, 2000);
      });
    };

    setTimeout(attempt, 2000);
  };

  //
  // Render

  render() {
    const {
      isFetching,
      error,
      settings,
      hasSettings,
      isTesting,
      successMessages,
      onInputChange,
      onSavePress,
      ...otherProps
    } = this.props;

    const {
      isResetConfirmModalOpen,
      isResetConfirmFinalModalOpen,
      isResetting,
      resetError
    } = this.state;

    const isMetadataServerUrlPending = !!(
      settings &&
      settings.metadataServerUrl &&
      settings.metadataServerUrl.pending
    );

    const showMetadataServerSuccess = !isMetadataServerUrlPending &&
      Array.isArray(successMessages) &&
      successMessages.length > 0;

    const showMetadataServerTesting = !isMetadataServerUrlPending && isTesting;

    const [metadataPrimaryMessage, ...metadataSecondaryMessages] =
      showMetadataServerSuccess ? successMessages : [];

    return (
      <PageContent title={translate('Development')}>
        <SettingsToolbarConnector
          {...otherProps}
          onSavePress={onSavePress}
        />

        <PageContentBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && error &&
              <div>
                {translate('DevelopmentSettingsUnableToLoad')}
              </div>
          }

          {
            hasSettings && !isFetching && !error &&
              <Form
                id="developmentSettings"
                {...otherProps}
                successMessages={[]}
              >
                <FieldSet legend={translate('MetadataProviderSource')}>
                  <FormGroup>
                    <FormLabel>
                      {translate('MetadataSource')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="metadataServerUrl"
                      helpText={translate('MetadataSourceHelpText')}
                      helpLink="https://discord.gg/nqFGsGUug2"
                      onChange={onInputChange}
                      {...settings.metadataServerUrl}
                    />
                  </FormGroup>

                  {
                    showMetadataServerTesting &&
                      <FormGroup>
                        <FormLabel>
                          {' '}
                        </FormLabel>

                        <div className={styles.metadataStatusContainer}>
                          <Alert
                            kind={kinds.INFO}
                            className={styles.metadataStatusAlert}
                          >
                            <div className={styles.metadataStatusHeader}>
                              <Icon
                                name={icons.SPINNER}
                                isSpinning={true}
                              />

                              <div className={styles.metadataStatusTitle}>
                                {translate('DevelopmentTestingMetadataServer')}
                              </div>
                            </div>
                          </Alert>
                        </div>
                      </FormGroup>
                  }

                  {
                    showMetadataServerSuccess &&
                      <FormGroup>
                        <FormLabel>
                          {' '}
                        </FormLabel>

                        <div className={styles.metadataStatusContainer}>
                          <Alert
                            kind={kinds.SUCCESS}
                            className={styles.metadataStatusAlert}
                          >
                            <div className={styles.metadataStatusHeader}>
                              <Icon
                                name={icons.CHECK_CIRCLE}
                                kind={kinds.SUCCESS}
                              />

                              <div className={styles.metadataStatusTitle}>
                                {metadataPrimaryMessage}
                              </div>
                            </div>

                            {
                              metadataSecondaryMessages.length > 0 &&
                                <div className={styles.metadataStatusLines}>
                                  {
                                    metadataSecondaryMessages.map((message, index) => {
                                      return (
                                        <div
                                          key={index}
                                          className={styles.metadataStatusLine}
                                        >
                                          {message}
                                        </div>
                                      );
                                    })
                                  }
                                </div>
                            }
                          </Alert>
                        </div>
                      </FormGroup>
                  }
                </FieldSet>

                <FieldSet legend={translate('Logging')}>
                  <FormGroup>
                    <FormLabel>
                      {translate('LogRotation')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.NUMBER}
                      name="logRotate"
                      helpText={translate('LogRotateHelpText')}
                      onChange={onInputChange}
                      {...settings.logRotate}
                    />
                  </FormGroup>

                  <FormGroup>
                    <FormLabel>
                      {translate('ConsoleLogLevel')}
                    </FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="consoleLogLevel"
                      values={logLevelOptions}
                      onChange={onInputChange}
                      {...settings.consoleLogLevel}
                    />
                  </FormGroup>

                  <FormGroup>
                    <FormLabel>
                      {translate('LogSQL')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="logSql"
                      helpText={translate('LogSqlHelpText')}
                      onChange={onInputChange}
                      {...settings.logSql}
                    />
                  </FormGroup>
                </FieldSet>

                <FieldSet legend={translate('Analytics')}>
                  <FormGroup>
                    <FormLabel>
                      {translate('FilterAnalyticsEvents')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="filterSentryEvents"
                      helpText={translate('FilterSentryEventsHelpText')}
                      onChange={onInputChange}
                      {...settings.filterSentryEvents}
                    />
                  </FormGroup>
                </FieldSet>

                <FieldSet legend={translate('DangerZone')}>
                  <FormGroup>
                    <FormLabel>
                      {translate('ResetEverything')}
                    </FormLabel>

                    <div>
                      <Alert kind={kinds.DANGER}>
                        {translate('ResetEverythingHelpText')}
                      </Alert>

                      {
                        resetError &&
                          <Alert kind={kinds.DANGER}>
                            {translate('ResetEverythingFailed')}
                          </Alert>
                      }

                      <Button
                        kind={kinds.DANGER}
                        onPress={this.onResetEverythingPress}
                        isDisabled={isResetting}
                      >
                        {
                          isResetting ?
                            translate('Resetting') :
                            translate('ResetEverything')
                        }
                      </Button>

                      {
                        isResetting &&
                          <div className={styles.resettingNote}>
                            <Icon
                              name={icons.SPINNER}
                              isSpinning={true}
                            />
                            <span>{translate('ResetEverythingRestartingNote')}</span>
                          </div>
                      }
                    </div>
                  </FormGroup>
                </FieldSet>
              </Form>
          }

          <ConfirmModal
            isOpen={isResetConfirmModalOpen}
            kind={kinds.DANGER}
            title={translate('ResetEverything')}
            message={translate('ResetEverythingConfirmMessage')}
            confirmLabel={translate('Continue')}
            onConfirm={this.onResetConfirmContinue}
            onCancel={this.onResetConfirmModalClose}
          />

          <ConfirmModal
            isOpen={isResetConfirmFinalModalOpen}
            kind={kinds.DANGER}
            title={translate('ResetEverythingFinalConfirmTitle')}
            message={translate('ResetEverythingFinalConfirmMessage')}
            confirmLabel={translate('ResetEverything')}
            onConfirm={this.onResetConfirmFinal}
            onCancel={this.onResetConfirmFinalModalClose}
          />
        </PageContentBody>
      </PageContent>
    );
  }

}

DevelopmentSettings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  isTesting: PropTypes.bool,
  successMessages: PropTypes.array,
  onSavePress: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default DevelopmentSettings;
