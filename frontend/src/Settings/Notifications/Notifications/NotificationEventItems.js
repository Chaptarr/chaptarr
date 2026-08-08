import PropTypes from 'prop-types';
import React from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './NotificationEventItems.css';

function NotificationEventItems(props) {
  const {
    item,
    onInputChange
  } = props;

  const {
    onGrab,
    onReleaseImport,
    onUpgrade,
    onRename,
    onAuthorAdded,
    onBookAdded,
    onAuthorDelete,
    onBookDelete,
    onBookFileDelete,
    onBookFileDeleteForUpgrade,
    onHealthIssue,
    onDownloadFailure,
    onImportFailure,
    onBookRetag,
    onApplicationUpdate,
    supportsOnGrab,
    supportsOnReleaseImport,
    supportsOnUpgrade,
    supportsOnRename,
    supportsOnAuthorAdded,
    supportsOnBookAdded,
    supportsOnAuthorDelete,
    supportsOnBookDelete,
    supportsOnBookFileDelete,
    supportsOnBookFileDeleteForUpgrade,
    supportsOnHealthIssue,
    includeHealthWarnings,
    supportsOnDownloadFailure,
    supportsOnImportFailure,
    supportsOnBookRetag,
    supportsOnApplicationUpdate
  } = item;

  const shouldShow = (supported) => supported;

  return (
    <FormGroup>
      <FormLabel>
        {translate('NotificationTriggers')}
      </FormLabel>
      <div>
        <FormInputHelpText
          text="Select which events should trigger this notification"
          link="https://discord.gg/nqFGsGUug2"
        />
        <div className={styles.events}>
          {
            shouldShow(supportsOnGrab.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onGrab"
                  helpText={translate('OnGrabHelpText')}
                  isDisabled={!supportsOnGrab.value}
                  {...onGrab}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnReleaseImport.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onReleaseImport"
                  helpText={translate('OnReleaseImportHelpText')}
                  isDisabled={!supportsOnReleaseImport.value}
                  {...onReleaseImport}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            onReleaseImport.value && shouldShow(supportsOnUpgrade.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onUpgrade"
                  helpText={translate('OnUpgradeHelpText')}
                  isDisabled={!supportsOnUpgrade.value}
                  {...onUpgrade}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnRename.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onRename"
                  helpText={translate('OnRenameHelpText')}
                  isDisabled={!supportsOnRename.value}
                  {...onRename}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnDownloadFailure.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onDownloadFailure"
                  helpText={translate('OnDownloadFailureHelpText')}
                  isDisabled={!supportsOnDownloadFailure.value}
                  {...onDownloadFailure}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnImportFailure.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onImportFailure"
                  helpText={translate('OnImportFailureHelpText')}
                  isDisabled={!supportsOnImportFailure.value}
                  {...onImportFailure}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnAuthorAdded.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onAuthorAdded"
                  helpText={translate('OnAuthorAddedHelpText')}
                  isDisabled={!supportsOnAuthorAdded.value}
                  {...onAuthorAdded}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnBookAdded.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onBookAdded"
                  helpText={translate('OnBookAddedHelpText')}
                  isDisabled={!supportsOnBookAdded.value}
                  {...onBookAdded}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnAuthorDelete.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onAuthorDelete"
                  helpText={translate('OnAuthorDeleteHelpText')}
                  isDisabled={!supportsOnAuthorDelete.value}
                  {...onAuthorDelete}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnBookDelete.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onBookDelete"
                  helpText={translate('OnBookDeleteHelpText')}
                  isDisabled={!supportsOnBookDelete.value}
                  {...onBookDelete}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnBookFileDelete.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onBookFileDelete"
                  helpText={translate('OnBookFileDeleteHelpText')}
                  isDisabled={!supportsOnBookFileDelete.value}
                  {...onBookFileDelete}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            onBookFileDelete.value && shouldShow(supportsOnBookFileDeleteForUpgrade.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onBookFileDeleteForUpgrade"
                  helpText={translate('OnBookFileDeleteForUpgradeHelpText')}
                  isDisabled={!supportsOnBookFileDeleteForUpgrade.value}
                  {...onBookFileDeleteForUpgrade}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnBookRetag.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onBookRetag"
                  helpText={translate('OnBookRetagHelpText')}
                  isDisabled={!supportsOnBookRetag.value}
                  {...onBookRetag}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnApplicationUpdate.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onApplicationUpdate"
                  helpText={translate('OnApplicationUpdateHelpText')}
                  isDisabled={!supportsOnApplicationUpdate.value}
                  {...onApplicationUpdate}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            shouldShow(supportsOnHealthIssue.value) &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="onHealthIssue"
                  helpText={translate('OnHealthIssueHelpText')}
                  isDisabled={!supportsOnHealthIssue.value}
                  {...onHealthIssue}
                  onChange={onInputChange}
                />
              </div>
          }

          {
            onHealthIssue.value &&
              <div>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="includeHealthWarnings"
                  helpText={translate('IncludeHealthWarningsHelpText')}
                  isDisabled={!supportsOnHealthIssue.value}
                  {...includeHealthWarnings}
                  onChange={onInputChange}
                />
              </div>
          }

        </div>
      </div>
    </FormGroup>
  );
}

NotificationEventItems.propTypes = {
  item: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default NotificationEventItems;
