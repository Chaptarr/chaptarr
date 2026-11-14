import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormInputGroup from 'Components/Form/FormInputGroup';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import LogsNavMenu from '../LogsNavMenu';
import LogFilesTableRow from './LogFilesTableRow';
import styles from './LogFiles.css';

const columns = [
  {
    name: 'filename',
    label: () => translate('Filename'),
    isVisible: true
  },
  {
    name: 'lastWriteTime',
    label: () => translate('LastWriteTime'),
    isVisible: true
  },
  {
    name: 'download',
    isVisible: true
  }
];

class LogFiles extends Component {

  //
  // Render

  render() {
    const {
      isFetching,
      items,
      deleteFilesExecuting,
      currentLogView,
      location,
      logLevel,
      isSavingSettings,
      saveSettingsError,
      settingsError,
      isSettingsLoaded,
      onRefreshPress,
      onDeleteFilesPress,
      onLogLevelChange,
      ...otherProps
    } = this.props;

    const logLevelOptions = [
      { key: 'trace', value: 'Trace' },
      { key: 'debug', value: 'Debug' },
      { key: 'info', value: 'Info' },
      { key: 'warn', value: 'Warn' },
      { key: 'error', value: 'Error' },
      { key: 'fatal', value: 'Fatal' },
      { key: 'off', value: 'Off' }
    ];

    return (
      <PageContent title={translate('LogFiles')}>
        <PageToolbar>
          <PageToolbarSection>
            <LogsNavMenu current={currentLogView} />

            <PageToolbarSeparator />

            <PageToolbarButton
              label={translate('Refresh')}
              iconName={icons.REFRESH}
              spinningName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={onRefreshPress}
            />

            <PageToolbarButton
              label={translate('Clear')}
              iconName={icons.CLEAR}
              isSpinning={deleteFilesExecuting}
              onPress={onDeleteFilesPress}
            />
          </PageToolbarSection>
        </PageToolbar>
        <PageContentBody>
          <Alert>
            <div className={styles.alertContent}>
              <div className={styles.locationInfo}>
                {translate('LogFilesLocatedIn', { location })}
              </div>

              {
                currentLogView === 'Log Files' &&
                  <div className={styles.logLevelContainer}>
                    <span>{translate('LogLevelLabel')}</span>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="logLevel"
                      value={logLevel}
                      values={logLevelOptions}
                      isDisabled={!isSettingsLoaded || isSavingSettings}
                      onChange={onLogLevelChange}
                      className={styles.logLevelDropdown}
                    />
                    {logLevel === 'trace' && (
                      <div className={styles.logLevelWarning}>
                        {translate('LogTraceWarning')}
                      </div>
                    )}
                  </div>
              }
            </div>
          </Alert>

          {settingsError && (
            <Alert kind={kinds.WARNING} role="alert">
              {translate('LogSettingsLoadFailed')}
            </Alert>
          )}

          {saveSettingsError && (
            <Alert kind={kinds.DANGER} role="alert">
              {translate('UnableToSaveLogLevel')}
            </Alert>
          )}

          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && !!items.length &&
              <div>
                <Table
                  columns={columns}
                  {...otherProps}
                >
                  <TableBody>
                    {
                      items.map((item) => {
                        return (
                          <LogFilesTableRow
                            key={item.id}
                            {...item}
                          />
                        );
                      })
                    }
                  </TableBody>
                </Table>
              </div>
          }

          {
            !isFetching && !items.length &&
              <Alert kind={kinds.INFO}>
                {translate('NoLogFiles')}
              </Alert>
          }
        </PageContentBody>
      </PageContent>
    );
  }

}

LogFiles.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  items: PropTypes.array.isRequired,
  deleteFilesExecuting: PropTypes.bool.isRequired,
  currentLogView: PropTypes.string.isRequired,
  location: PropTypes.string.isRequired,
  logLevel: PropTypes.string,
  isSavingSettings: PropTypes.bool,
  saveSettingsError: PropTypes.object,
  settingsError: PropTypes.object,
  isSettingsLoaded: PropTypes.bool,
  onRefreshPress: PropTypes.func.isRequired,
  onDeleteFilesPress: PropTypes.func.isRequired,
  onLogLevelChange: PropTypes.func.isRequired
};

export default LogFiles;
