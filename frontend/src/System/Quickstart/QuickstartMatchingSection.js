import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormInputGroup from 'Components/Form/FormInputGroup';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds } from 'Helpers/Props';
import { fetchMediaManagementSettings, saveMediaManagementSettings, setMediaManagementSettingsValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import styles from './Quickstart.css';

const SECTION = 'mediaManagement';

const matchingExamples = [
  {
    mode: 'strict',
    label: 'BookMatchingStrictnessStrict',
    fileParts: [
      { text: 'BookTitlePlaceholder', kind: 'title' },
      { text: 'KnownAccurateMetadataPlaceholder', kind: 'allowed' }
    ],
    blockParts: [
      { text: 'UnexplainedExtraTextPlaceholder', kind: 'conflict' },
      { text: 'ConflictingBookDataPlaceholder', kind: 'conflict' },
      { text: 'SameSeriesDifferentPositionPlaceholder', kind: 'conflict' }
    ],
    explanation: 'QuickstartMatchingStrictExample'
  },
  {
    mode: 'balanced',
    label: 'BookMatchingStrictnessBalanced',
    fileParts: [
      { text: 'BookTitlePlaceholder', kind: 'title' },
      { text: 'AccurateMetadataPlaceholder', kind: 'allowed' },
      { text: 'ExtraUnrelatedTextPlaceholder', kind: 'allowed' },
      { text: 'SameSeriesDifferentPositionPlaceholder', kind: 'allowed' }
    ],
    blockParts: [
      { text: 'SiblingTitleBetterMatchPlaceholder', kind: 'conflict' },
      { text: 'GenericSeriesTitleWithDifferentPositionPlaceholder', kind: 'conflict' }
    ],
    explanation: 'QuickstartMatchingBalancedExample'
  },
  {
    mode: 'aggressive',
    label: 'BookMatchingStrictnessAggressive',
    fileParts: [
      { text: 'BookTitlePlaceholder', kind: 'title' },
      { text: 'SameSeriesDifferentPositionPlaceholder', kind: 'allowed' },
      { text: 'NoisyExtraTextPlaceholder', kind: 'allowed' }
    ],
    blockParts: [
      { text: 'SiblingTitleBetterMatchPlaceholder', kind: 'conflict' },
      { text: 'GenericSeriesTitleWithDifferentPositionPlaceholder', kind: 'conflict' }
    ],
    explanation: 'QuickstartMatchingLooseExample'
  }
];

function renderMatchingExampleTokens(example, labelKey, parts, fieldKind) {
  const isBlockSection = fieldKind === 'Block';
  const sectionClassName = isBlockSection ? styles.matchingExampleFieldBlock : styles.matchingExampleFieldAllow;

  return (
    <div className={`${styles.matchingExampleField} ${sectionClassName}`}>
      <span className={styles.matchingExampleFieldLabel}>
        {translate(labelKey)}
      </span>

      <span className={styles.matchingExampleTokens}>
        {parts.map((part, index) => {
          let tokenClassName = styles.matchingTokenAllowed;
          if (isBlockSection) {
            tokenClassName = styles.matchingTokenConflict;
          } else if (part.kind === 'title') {
            tokenClassName = styles.matchingTokenTitle;
          }

          return (
            <span
              key={`${example.mode}-${labelKey}-${index}`}
              className={`${styles.matchingToken} ${tokenClassName}`}
            >
              {translate(part.text)}
            </span>
          );
        })}
      </span>
    </div>
  );
}

function renderMatchingExample(example, selectedMode) {
  const isSelected = example.mode === selectedMode;

  return (
    <div
      key={example.mode}
      className={`${styles.matchingExample} ${isSelected ? styles.matchingExampleSelected : ''}`}
    >
      <div className={styles.matchingExampleHeader}>
        <span>{translate(example.label)}</span>
      </div>

      <div className={styles.matchingExampleExplanation}>
        {translate(example.explanation)}
      </div>

      {renderMatchingExampleTokens(example, 'QuickstartMatchingAllowsLabel', example.fileParts, 'Allow')}
      {renderMatchingExampleTokens(example, 'QuickstartMatchingBlocksLabel', example.blockParts, 'Block')}
    </div>
  );
}

class QuickstartMatchingSection extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      didRequestSave: false
    };
  }

  componentDidMount() {
    this.props.fetchMediaManagementSettings();
  }

  componentDidUpdate(prevProps) {
    if (this.state.didRequestSave && prevProps.isSaving && !this.props.isSaving) {
      const { markSectionInteracted, saveError } = this.props;

      this.setState({ didRequestSave: false });

      if (!saveError && markSectionInteracted) {
        markSectionInteracted({ section: 'matching' });
      }
    }
  }

  onInputChange = ({ name, value }) => {
    const { setMediaManagementSettingsValue: setMediaManagementSettingsValueAction, settings } = this.props;

    if (name === 'bookMatchingStrictness') {
      setMediaManagementSettingsValueAction({ name, value });

      if (value === 'strict') {
        setMediaManagementSettingsValueAction({ name: 'usePathAsTagsFallback', value: false });
      }

      return;
    }

    if (name === 'usePathAsTagsFallback' &&
        settings.bookMatchingStrictness?.value === 'strict' &&
        value) {
      return;
    }

    setMediaManagementSettingsValueAction({ name, value });
  };

  onSavePress = () => {
    this.setState({ didRequestSave: true });
    this.props.saveMediaManagementSettings();
  };

  render() {
    const {
      isFetching,
      error,
      hasSettings,
      hasPendingChanges,
      isSaving,
      saveError,
      settings,
      quickstartState
    } = this.props;

    const interactions = quickstartState?.interactions || {};
    const bookMatchingStrictness = settings.bookMatchingStrictness || { value: 'balanced' };
    const usePathAsTagsFallback = settings.usePathAsTagsFallback || { value: true };
    const isStrictMatching = bookMatchingStrictness.value === 'strict';

    if (isFetching && !hasSettings) {
      return (
        <QuickstartSection
          sectionKey="matching"
          title={`6. ${translate('Matching')}`}
        >
          <LoadingIndicator />
        </QuickstartSection>
      );
    }

    return (
      <QuickstartSection
        sectionKey="matching"
        title={`6. ${translate('Matching')}`}
        isComplete={!!interactions.matching}
      >
        {error && (
          <Alert kind={kinds.DANGER}>
            {translate('UnableToLoadMediaManagementSettings')}
          </Alert>
        )}

        {saveError && (
          <Alert kind={kinds.DANGER}>
            {translate('UnableToSaveMediaManagementSettings')}
          </Alert>
        )}

        {hasSettings && !error && (
          <Form id="quickstartMatchingSettings">
            <div className={styles.quickstartMatchingControls}>
              <div className={styles.subsectionHeader}>
                {translate('BookMatchingStrictness')}
              </div>

              <div className={styles.quickstartMatchingControl}>
                <div className={styles.quickstartMatchingSelect}>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="bookMatchingStrictness"
                    values={[
                      { key: 'strict', value: translate('BookMatchingStrictnessStrict') },
                      { key: 'balanced', value: translate('BookMatchingStrictnessBalanced') },
                      { key: 'aggressive', value: translate('BookMatchingStrictnessAggressive') }
                    ]}
                    onChange={this.onInputChange}
                    {...bookMatchingStrictness}
                  />
                </div>

                <div className={styles.matchingExamplesContext}>
                  {translate('QuickstartMatchingExamplesContext')}
                </div>

                <div className={styles.matchingExamples}>
                  {matchingExamples.map((example) => renderMatchingExample(example, bookMatchingStrictness.value))}
                </div>
              </div>

              <div className={styles.quickstartMatchingControl}>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="usePathAsTagsFallback"
                  helpText={translate('UsePathAsTagsFallbackHelpText')}
                  isDisabled={isStrictMatching}
                  onChange={this.onInputChange}
                  {...usePathAsTagsFallback}
                />

                {isStrictMatching && (
                  <div className={`${styles.quickstartMatchingHelpText} ${styles.quickstartMatchingHelpTextIndented}`}>
                    {translate('UsePathAsTagsFallbackStrictDisabledHelpText')}
                  </div>
                )}
              </div>
            </div>
          </Form>
        )}

        {hasSettings && (hasPendingChanges || isSaving) && (
          <div className={`${styles.quickstartCardActions} ${styles.quickstartMatchingActions}`}>
            <SpinnerButton
              className={styles.quickstartCardButton}
              isSpinning={isSaving}
              isDisabled={!!error || !hasPendingChanges}
              onPress={this.onSavePress}
            >
              {translate('Save')}
            </SpinnerButton>
          </div>
        )}
      </QuickstartSection>
    );
  }
}

QuickstartMatchingSection.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  hasSettings: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  settings: PropTypes.object.isRequired,
  quickstartState: PropTypes.object,
  fetchMediaManagementSettings: PropTypes.func.isRequired,
  saveMediaManagementSettings: PropTypes.func.isRequired,
  setMediaManagementSettingsValue: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func
};

function createMapStateToProps() {
  return createSelector(
    createSettingsSectionSelector(SECTION),
    (sectionSettings) => {
      return {
        ...sectionSettings
      };
    }
  );
}

const mapDispatchToProps = {
  fetchMediaManagementSettings,
  saveMediaManagementSettings,
  setMediaManagementSettingsValue
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartMatchingSection);
