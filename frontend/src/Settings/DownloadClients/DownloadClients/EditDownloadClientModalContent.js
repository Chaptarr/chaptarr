import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import FormLabel from 'Components/Form/FormLabel';
import ProviderFieldFormGroup from 'Components/Form/ProviderFieldFormGroup';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerErrorButton from 'Components/Link/SpinnerErrorButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, inputTypes, kinds, sizes } from 'Helpers/Props';
import AdvancedSettingsButton from 'Settings/AdvancedSettingsButton';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';
import styles from './EditDownloadClientModalContent.css';

function LinkToggleButton({ isLinked, onPress, isLastButton, title, className }) {
  return (
    <Button
      className={classNames(
        styles.linkToggleButton,
        !isLastButton && styles.linkToggleMiddleButton,
        className
      )}
      kind={kinds.DEFAULT}
      size={sizes.SMALL}
      onPress={onPress}
      title={title}
      aria-label={title}
    >
      <Icon
        name={isLinked ? icons.LINK : icons.LINK_SLASH}
        size={12}
        fixedWidth={true}
      />
    </Button>
  );
}

class EditDownloadClientModalContent extends Component {
  constructor(props) {
    super(props);

    this.state = {
      linkCategories: this.areProviderFieldsLinked(props.item?.fields, 'audiobookCategory', 'ebookCategory'),
      linkPostImportCategories: this.areProviderFieldsLinked(props.item?.fields, 'audiobookImportedCategory', 'ebookImportedCategory'),
      linkClientTags: this.areProviderFieldsLinked(props.item?.fields, 'audiobookTags', 'ebookTags'),
      linkTags: this.areValuesLinked(props.item?.audiobookTags?.value, props.item?.ebookTags?.value)
    };
  }

  componentDidUpdate(prevProps) {
    if (prevProps.item?.id !== this.props.item?.id) {
      this.setState({
        linkCategories: this.areProviderFieldsLinked(this.props.item?.fields, 'audiobookCategory', 'ebookCategory'),
        linkPostImportCategories: this.areProviderFieldsLinked(this.props.item?.fields, 'audiobookImportedCategory', 'ebookImportedCategory'),
        linkClientTags: this.areProviderFieldsLinked(this.props.item?.fields, 'audiobookTags', 'ebookTags'),
        linkTags: this.areValuesLinked(this.props.item?.audiobookTags?.value, this.props.item?.ebookTags?.value)
      });
    }
  }

  normalizeProviderFieldValueForLinking(value) {
    if (value == null) {
      return '';
    }

    if (Array.isArray(value)) {
      return value
        .filter((v) => v != null && v !== '')
        .map((v) => `${v}`.toLowerCase())
        .sort()
        .join('|');
    }

    return `${value}`.toLowerCase();
  }

  areProviderFieldsLinked(fields, fieldA, fieldB) {
    if (!fields || !fields.length) {
      return false;
    }

    const a = fields.find((f) => f.name === fieldA);
    const b = fields.find((f) => f.name === fieldB);

    if (!a || !b) {
      return false;
    }

    return this.normalizeProviderFieldValueForLinking(a.value) === this.normalizeProviderFieldValueForLinking(b.value);
  }

  areValuesLinked(valueA, valueB) {
    return this.normalizeProviderFieldValueForLinking(valueA) === this.normalizeProviderFieldValueForLinking(valueB);
  }

  getProviderFieldValue(name) {
    const { item } = this.props;
    const fields = item?.fields || [];
    return fields.find((f) => f.name === name)?.value;
  }

  getInputValue(name) {
    return this.props.item?.[name]?.value;
  }

  onLinkedProviderFieldChange = (linkKey, fieldA, fieldB, change) => {
    const { onFieldChange } = this.props;
    const isLinked = this.state[linkKey];
    const { name, value } = change;

    onFieldChange({ name, value });

    if (!isLinked) {
      return;
    }

    if (name === fieldA) {
      onFieldChange({ name: fieldB, value });
    } else if (name === fieldB) {
      onFieldChange({ name: fieldA, value });
    }
  };

  onLinkedInputChange = (linkKey, fieldA, fieldB, change) => {
    const { onInputChange } = this.props;
    const isLinked = this.state[linkKey];
    const { name, value } = change;

    onInputChange({ name, value });

    if (!isLinked) {
      if (fieldA === 'audiobookTags' && fieldB === 'ebookTags') {
        const audiobookValue = name === fieldA ? value : (this.getInputValue(fieldA) ?? []);
        const ebookValue = name === fieldB ? value : (this.getInputValue(fieldB) ?? []);
        const tagsUnion = Array.from(new Set([...(audiobookValue ?? []), ...(ebookValue ?? [])]));
        onInputChange({ name: 'tags', value: tagsUnion });
      }

      return;
    }

    if (name === fieldA) {
      onInputChange({ name: fieldB, value });
    } else if (name === fieldB) {
      onInputChange({ name: fieldA, value });
    }

    if (fieldA === 'audiobookTags' && fieldB === 'ebookTags') {
      const tagsUnion = Array.from(new Set([...(value ?? [])]));
      onInputChange({ name: 'tags', value: tagsUnion });
    }
  };

  onToggleLink = (linkKey, fieldA, fieldB) => {
    const { onFieldChange } = this.props;
    const isLinked = this.state[linkKey];
    const nextLinked = !isLinked;

    this.setState({ [linkKey]: nextLinked });

    if (!nextLinked) {
      return;
    }

    const aValue = this.getProviderFieldValue(fieldA);
    const bValue = this.getProviderFieldValue(fieldB);
    const valueToSync = aValue != null ? aValue : bValue;

    onFieldChange({ name: fieldA, value: valueToSync });
    onFieldChange({ name: fieldB, value: valueToSync });
  };

  onToggleInputLink = (linkKey, fieldA, fieldB) => {
    const { onInputChange } = this.props;
    const isLinked = this.state[linkKey];
    const nextLinked = !isLinked;

    this.setState({ [linkKey]: nextLinked });

    if (!nextLinked) {
      return;
    }

    const aValue = this.getInputValue(fieldA);
    const bValue = this.getInputValue(fieldB);
    const valueToSync = aValue != null ? aValue : bValue;

    onInputChange({ name: fieldA, value: valueToSync });
    onInputChange({ name: fieldB, value: valueToSync });

    if (fieldA === 'audiobookTags' && fieldB === 'ebookTags') {
      onInputChange({ name: 'tags', value: valueToSync ?? [] });
    }
  };

  //
  // Render

  render() {
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
      onAdvancedSettingsPress,
      onDeleteDownloadClientPress,
      ...otherProps
    } = this.props;

    const {
      id,
      implementationName,
      name,
      enable,
      protocol,
      priority,
      audiobookTags,
      ebookTags,
      removeCompletedDownloads,
      removeFailedDownloads,
      copyUnmanagedDownloads,
      fields,
      tags,
      message
    } = item;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {`${id ? 'Edit' : 'Add'} Download Client - ${implementationName}`}
        </ModalHeader>

        <ModalBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && !!error &&
              <div>
                {translate('UnableToAddANewDownloadClientPleaseTryAgain')}
              </div>
          }

          {
            !isFetching && !error &&
              <Form {...otherProps}>
                {
                  message && message.value &&
                    <Alert
                      className={styles.message}
                      kind={message.value.type}
                    >
                      {message.value.message}
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
                    {translate('Enable')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="enable"
                    {...enable}
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

                {
                  fields && (() => {
                    const rendered = new Set();
                    const fieldMap = new Map(fields.map((f) => [f.name, f]));

                    return fields.map((field) => {
                      if (rendered.has(field.name)) {
                        return null;
                      }

                      if (field.name === 'audiobookCategory' && fieldMap.has('ebookCategory')) {
                        rendered.add('audiobookCategory');
                        rendered.add('ebookCategory');

                        const audiobookField = fieldMap.get('audiobookCategory');
                        const ebookField = fieldMap.get('ebookCategory');
                        const isLinked = this.state.linkCategories;
                        const sharedHelpText = audiobookField.helpText || ebookField.helpText;
                        const sharedHelpTextWarning = audiobookField.helpTextWarning || ebookField.helpTextWarning;
                        const sharedHelpLink = audiobookField.helpLink || ebookField.helpLink;

                        return (
                          <React.Fragment key="categoryPair">
                            <div className={styles.linkedPairContainer}>
                              <div className={styles.linkedPairFields}>
                                <ProviderFieldFormGroup
                                  key={audiobookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...audiobookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkCategories', 'audiobookCategory', 'ebookCategory', change)}
                                />

                                <ProviderFieldFormGroup
                                  key={ebookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...ebookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkCategories', 'audiobookCategory', 'ebookCategory', change)}
                                />
                              </div>

                              <div className={styles.linkedPairConnector}>
                                <div className={styles.linkedPairLine} />
                                <LinkToggleButton
                                  className={styles.linkedPairToggle}
                                  isLinked={isLinked}
                                  isLastButton={true}
                                  title={isLinked ? 'Unlink categories' : 'Link categories'}
                                  onPress={() => this.onToggleLink('linkCategories', 'audiobookCategory', 'ebookCategory')}
                                />
                                <div className={styles.linkedPairLine} />
                              </div>
                            </div>

                            {
                              sharedHelpText &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpText}
                                  link={sharedHelpLink}
                                />
                            }

                            {
                              !sharedHelpText && sharedHelpTextWarning &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpTextWarning}
                                  link={sharedHelpLink}
                                  isWarning={true}
                                />
                            }
                          </React.Fragment>
                        );
                      }

                      if (field.name === 'audiobookImportedCategory' && fieldMap.has('ebookImportedCategory')) {
                        rendered.add('audiobookImportedCategory');
                        rendered.add('ebookImportedCategory');

                        const audiobookField = fieldMap.get('audiobookImportedCategory');
                        const ebookField = fieldMap.get('ebookImportedCategory');
                        const isLinked = this.state.linkPostImportCategories;
                        const sharedHelpText = audiobookField.helpText || ebookField.helpText;
                        const sharedHelpTextWarning = audiobookField.helpTextWarning || ebookField.helpTextWarning;
                        const sharedHelpLink = audiobookField.helpLink || ebookField.helpLink;

                        return (
                          <React.Fragment key="postImportCategoryPair">
                            <div className={styles.linkedPairContainer}>
                              <div className={styles.linkedPairFields}>
                                <ProviderFieldFormGroup
                                  key={audiobookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...audiobookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkPostImportCategories', 'audiobookImportedCategory', 'ebookImportedCategory', change)}
                                />

                                <ProviderFieldFormGroup
                                  key={ebookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...ebookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkPostImportCategories', 'audiobookImportedCategory', 'ebookImportedCategory', change)}
                                />
                              </div>

                              <div className={styles.linkedPairConnector}>
                                <div className={styles.linkedPairLine} />
                                <LinkToggleButton
                                  className={styles.linkedPairToggle}
                                  isLinked={isLinked}
                                  isLastButton={true}
                                  title={isLinked ? 'Unlink post-import categories' : 'Link post-import categories'}
                                  onPress={() => this.onToggleLink('linkPostImportCategories', 'audiobookImportedCategory', 'ebookImportedCategory')}
                                />
                                <div className={styles.linkedPairLine} />
                              </div>
                            </div>

                            {
                              sharedHelpText &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpText}
                                  link={sharedHelpLink}
                                />
                            }

                            {
                              !sharedHelpText && sharedHelpTextWarning &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpTextWarning}
                                  link={sharedHelpLink}
                                  isWarning={true}
                                />
                            }
                          </React.Fragment>
                        );
                      }

                      if (field.name === 'audiobookTags' && fieldMap.has('ebookTags')) {
                        rendered.add('audiobookTags');
                        rendered.add('ebookTags');

                        const audiobookField = fieldMap.get('audiobookTags');
                        const ebookField = fieldMap.get('ebookTags');
                        const isLinked = this.state.linkClientTags;
                        const sharedHelpText = audiobookField.helpText || ebookField.helpText;
                        const sharedHelpTextWarning = audiobookField.helpTextWarning || ebookField.helpTextWarning;
                        const sharedHelpLink = audiobookField.helpLink || ebookField.helpLink;

                        return (
                          <React.Fragment key="tagPair">
                            <div className={styles.linkedPairContainer}>
                              <div className={styles.linkedPairFields}>
                                <ProviderFieldFormGroup
                                  key={audiobookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...audiobookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkClientTags', 'audiobookTags', 'ebookTags', change)}
                                />

                                <ProviderFieldFormGroup
                                  key={ebookField.name}
                                  advancedSettings={advancedSettings}
                                  provider="downloadClient"
                                  providerData={item}
                                  {...ebookField}
                                  helpText={null}
                                  helpTextWarning={null}
                                  helpLink={null}
                                  onChange={(change) => this.onLinkedProviderFieldChange('linkClientTags', 'audiobookTags', 'ebookTags', change)}
                                />
                              </div>

                              <div className={styles.linkedPairConnector}>
                                <div className={styles.linkedPairLine} />
                                <LinkToggleButton
                                  className={styles.linkedPairToggle}
                                  isLinked={isLinked}
                                  isLastButton={true}
                                  title={isLinked ? 'Unlink tags' : 'Link tags'}
                                  onPress={() => this.onToggleLink('linkClientTags', 'audiobookTags', 'ebookTags')}
                                />
                                <div className={styles.linkedPairLine} />
                              </div>
                            </div>

                            {
                              sharedHelpText &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpText}
                                  link={sharedHelpLink}
                                />
                            }

                            {
                              !sharedHelpText && sharedHelpTextWarning &&
                                <FormInputHelpText
                                  className={styles.linkedPairHelpText}
                                  text={sharedHelpTextWarning}
                                  link={sharedHelpLink}
                                  isWarning={true}
                                />
                            }
                          </React.Fragment>
                        );
                      }

                      rendered.add(field.name);

                      return (
                        <ProviderFieldFormGroup
                          key={field.name}
                          advancedSettings={advancedSettings}
                          provider="downloadClient"
                          providerData={item}
                          {...field}
                          onChange={onFieldChange}
                        />
                      );
                    });
                  })()
                }

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                >
                  <FormLabel>
                    {translate('ClientPriority')}
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

                {(() => {
                  const fallbackTagsValue = tags?.value ?? [];
                  const fallbackTagsErrors = tags?.errors ?? [];
                  const fallbackTagsWarnings = tags?.warnings ?? [];
                  const normalizedAudiobookTags = audiobookTags ?? { value: fallbackTagsValue, errors: fallbackTagsErrors, warnings: fallbackTagsWarnings };
                  const normalizedEbookTags = ebookTags ?? { value: fallbackTagsValue, errors: fallbackTagsErrors, warnings: fallbackTagsWarnings };

                  return (
                    <React.Fragment>
                      <div className={styles.linkedPairContainer}>
                        <div className={styles.linkedPairFields}>
                          <FormGroup>
                            <FormLabel>{translate('AudiobookTags')}</FormLabel>
                            <FormInputGroup
                              type={inputTypes.TAG}
                              name="audiobookTags"
                              helpText={null}
                              helpTextWarning={null}
                              helpLink={null}
                              {...normalizedAudiobookTags}
                              onChange={(change) => this.onLinkedInputChange('linkTags', 'audiobookTags', 'ebookTags', change)}
                            />
                          </FormGroup>

                          <FormGroup>
                            <FormLabel>{translate('EbookTags')}</FormLabel>
                            <FormInputGroup
                              type={inputTypes.TAG}
                              name="ebookTags"
                              helpText={null}
                              helpTextWarning={null}
                              helpLink={null}
                              {...normalizedEbookTags}
                              onChange={(change) => this.onLinkedInputChange('linkTags', 'audiobookTags', 'ebookTags', change)}
                            />
                          </FormGroup>
                        </div>

                        <div className={styles.linkedPairConnector}>
                          <div className={styles.linkedPairLine} />
                          <LinkToggleButton
                            className={styles.linkedPairToggle}
                            isLinked={this.state.linkTags}
                            isLastButton={true}
                            title={this.state.linkTags ? 'Unlink tags' : 'Link tags'}
                            onPress={() => this.onToggleInputLink('linkTags', 'audiobookTags', 'ebookTags')}
                          />
                          <div className={styles.linkedPairLine} />
                        </div>
                      </div>

                      <FormInputHelpText
                        className={styles.linkedPairHelpText}
                        text={translate('DownloadClientTagHelpText')}
                      />
                    </React.Fragment>
                  );
                })()}

                <FieldSet
                  size={sizes.SMALL}
                  legend={translate('CompletedDownloadHandling')}
                >
                  <FormGroup>
                    <FormLabel>{translate('RemoveCompleted')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="removeCompletedDownloads"
                      helpText={translate('RemoveCompletedDownloadsHelpText')}
                      {...removeCompletedDownloads}
                      onChange={onInputChange}
                    />
                  </FormGroup>

                  {
                    protocol && protocol.value === 'torrent' &&
                      <FormGroup>
                        <FormLabel>{translate('CopyUnmanagedDownloads')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="copyUnmanagedDownloads"
                          helpText={translate('CopyUnmanagedDownloadsHelpText')}
                          {...copyUnmanagedDownloads}
                          onChange={onInputChange}
                        />
                      </FormGroup>
                  }

                  {
                    protocol && protocol.value !== 'torrent' &&
                      <FormGroup>
                        <FormLabel>{translate('RemoveFailed')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="removeFailedDownloads"
                          helpText={translate('RemoveFailedDownloadsHelpText')}
                          {...removeFailedDownloads}
                          onChange={onInputChange}
                        />
                      </FormGroup>
                  }
                </FieldSet>
              </Form>
          }
        </ModalBody>
        <ModalFooter>
          {
            id &&
              <Button
                className={styles.deleteButton}
                kind={kinds.DANGER}
                onPress={onDeleteDownloadClientPress}
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
}

EditDownloadClientModalContent.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isTesting: PropTypes.bool.isRequired,
  item: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onFieldChange: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onTestPress: PropTypes.func.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onDeleteDownloadClientPress: PropTypes.func
};

export default EditDownloadClientModalContent;
