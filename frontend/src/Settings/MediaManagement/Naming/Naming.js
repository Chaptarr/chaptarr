import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputButton from 'Components/Form/FormInputButton';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import NamingModal from './NamingModal';
import NamingVisualBuilder from './NamingVisualBuilder/NamingVisualBuilder';
import styles from './Naming.css';

class Naming extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isNamingModalOpen: false,
      namingModalOptions: null,
      isVisualBuilderOpen: false
    };
  }

  //
  // Listeners

  onStandardNamingModalOpenClick = () => {
    const { selectedMediaType } = this.props;
    const standardBookFormatName = selectedMediaType === 'ebook' ? 'ebookStandardBookFormat' : 'standardBookFormat';

    this.setState({
      isNamingModalOpen: true,
      namingModalOptions: {
        name: standardBookFormatName,
        book: true,
        additional: true
      }
    });
  };

  onAuthorFolderNamingModalOpenClick = () => {
    const { selectedMediaType } = this.props;
    const authorFolderFormatName = selectedMediaType === 'ebook' ? 'ebookAuthorFolderFormat' : 'authorFolderFormat';

    this.setState({
      isNamingModalOpen: true,
      namingModalOptions: {
        name: authorFolderFormatName
      }
    });
  };

  onNamingModalClose = () => {
    this.setState({ isNamingModalOpen: false });
  };

  onVisualBuilderOpenClick = () => {
    this.setState({ isVisualBuilderOpen: true });
  };

  onVisualBuilderClose = () => {
    this.setState({ isVisualBuilderOpen: false });
  };

  onVisualBuilderSave = (pattern) => {
    const { onInputChange, selectedMediaType } = this.props;
    const standardBookFormatName = selectedMediaType === 'ebook' ? 'ebookStandardBookFormat' : 'standardBookFormat';

    // Update the actual form value
    onInputChange({
      name: standardBookFormatName,
      value: pattern
    });
    // Close the modal
    this.setState({ isVisualBuilderOpen: false });
  };

  //
  // Render

  render() {
    const {
      advancedSettings,
      isFetching,
      error,
      settings,
      hasSettings,
      examples,
      examplesPopulated,
      onInputChange,
      selectedMediaType,
      onMediaTypeChange,
      hasMediaManagementSettings,
      mediaManagementSettings,
      onMediaManagementInputChange
    } = this.props;

    const {
      isNamingModalOpen,
      namingModalOptions,
      isVisualBuilderOpen
    } = this.state;

    const isEbook = selectedMediaType === 'ebook';

    const renameBooksName = isEbook ? 'ebookRenameBooks' : 'renameBooks';
    const replaceIllegalCharactersName = isEbook ? 'ebookReplaceIllegalCharacters' : 'replaceIllegalCharacters';
    const colonReplacementFormatName = isEbook ? 'ebookColonReplacementFormat' : 'colonReplacementFormat';
    const standardBookFormatName = isEbook ? 'ebookStandardBookFormat' : 'standardBookFormat';
    const authorFolderFormatName = isEbook ? 'ebookAuthorFolderFormat' : 'authorFolderFormat';

    const renameBooksSetting = hasSettings ? (settings[renameBooksName] || settings.renameBooks) : null;
    const replaceIllegalCharactersSetting = hasSettings ? (settings[replaceIllegalCharactersName] || settings.replaceIllegalCharacters) : null;
    const colonReplacementFormatSetting = hasSettings ? (settings[colonReplacementFormatName] || settings.colonReplacementFormat) : null;
    const standardBookFormatSetting = hasSettings ? (settings[standardBookFormatName] || settings.standardBookFormat) : null;
    const authorFolderFormatSetting = hasSettings ? (settings[authorFolderFormatName] || settings.authorFolderFormat) : null;

    const renameBooks = renameBooksSetting?.value;
    const replaceIllegalCharacters = replaceIllegalCharactersSetting?.value;

    const colonReplacementOptions = [
      { key: 0, value: translate('Delete') },
      { key: 1, value: translate('ReplaceWithDash') },
      { key: 2, value: translate('ReplaceWithSpaceDash') },
      { key: 3, value: translate('ReplaceWithSpaceDashSpace') },
      { key: 4, value: translate('SmartReplace'), hint: translate('DashOrSpaceDashDependingOnName') }
    ];

    const standardBookFormatHelpTexts = ['Empty tokens and surrounding punctuation are automatically omitted'];
    const standardBookFormatErrors = [];
    const authorFolderFormatHelpTexts = [];
    const authorFolderFormatErrors = [];

    if (examplesPopulated) {
      if (examples.singleBookExample) {
        standardBookFormatHelpTexts.push(`Single Book: ${examples.singleBookExample}`);
      } else {
        standardBookFormatErrors.push({ message: 'Single Book: Invalid Format' });
      }

      if (examples.multiPartBookExample) {
        standardBookFormatHelpTexts.push(`Multi-part Book: ${examples.multiPartBookExample}`);
      } else {
        standardBookFormatErrors.push({ message: 'Multi-part Book: Invalid Format' });
      }

      if (examples.authorFolderExample) {
        authorFolderFormatHelpTexts.push(`Example: ${examples.authorFolderExample}`);
      } else {
        authorFolderFormatErrors.push({ message: 'Invalid Format' });
      }
    }

    return (
      <FieldSet legend={translate('BookNaming')}>
        {
          isFetching &&
            <LoadingIndicator />
        }

        {
          !isFetching && error &&
            <Alert kind={kinds.DANGER}>
              {translate('UnableToLoadNamingSettings')}
            </Alert>
        }

        {
          hasSettings && !isFetching && !error &&
            <Form>
              <MediaTypeToggle
                selectedMediaType={selectedMediaType}
                onMediaTypeChange={onMediaTypeChange}
              />

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>
                  {translate('RenameBooks')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name={renameBooksName}
                  helpText={translate('RenameBooksHelpText')}
                  onChange={onInputChange}
                  {...renameBooksSetting}
                />
              </FormGroup>

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>
                  {translate('ReplaceIllegalCharacters')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name={replaceIllegalCharactersName}
                  helpText={translate('ReplaceIllegalCharactersHelpText')}
                  onChange={onInputChange}
                  {...replaceIllegalCharactersSetting}
                />
              </FormGroup>

              {
                replaceIllegalCharacters ?
                  <FormGroup>
                    <FormLabel>
                      {translate('ColonReplacement')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name={colonReplacementFormatName}
                      values={colonReplacementOptions}
                      onChange={onInputChange}
                      {...colonReplacementFormatSetting}
                    />
                  </FormGroup> :
                  null
              }

              <FormGroup
                size={sizes.LARGE}
                advancedSettings={advancedSettings}
                isAdvanced={!renameBooks}
              >
                <FormLabel>
                  {translate('StandardBookFormat')}
                </FormLabel>

                <FormInputGroup
                  inputClassName={styles.namingInput}
                  type={inputTypes.TEXT}
                  name={standardBookFormatName}
                  buttons={[
                    // Visual builder temporarily disabled - the existing token system is robust
                    // <FormInputButton onPress={this.onVisualBuilderOpenClick}>🎨</FormInputButton>,
                    <FormInputButton onPress={this.onStandardNamingModalOpenClick}>?</FormInputButton>
                  ]}
                  onChange={onInputChange}
                  {...standardBookFormatSetting}
                  helpTexts={standardBookFormatHelpTexts}
                  errors={[...standardBookFormatErrors, ...standardBookFormatSetting.errors]}
                />
              </FormGroup>

              {
                hasMediaManagementSettings &&
                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>
                      {translate('CreateEmptyAuthorFolders')}
                    </FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name={isEbook ? 'createEmptyEbookAuthorFolders' : 'createEmptyAuthorFolders'}
                      helpText={translate('CreateEmptyAuthorFoldersHelpText')}
                      onChange={onMediaManagementInputChange}
                      {...mediaManagementSettings[isEbook ? 'createEmptyEbookAuthorFolders' : 'createEmptyAuthorFolders']}
                    />
                  </FormGroup>
              }

              <FormGroup
                advancedSettings={advancedSettings}
                isAdvanced={true}
              >
                <FormLabel>
                  {translate('AuthorFolderFormat')}
                </FormLabel>

                <FormInputGroup
                  inputClassName={styles.namingInput}
                  type={inputTypes.TEXT}
                  name={authorFolderFormatName}
                  buttons={<FormInputButton onPress={this.onAuthorFolderNamingModalOpenClick}>?</FormInputButton>}
                  onChange={onInputChange}
                  {...authorFolderFormatSetting}
                  helpTexts={['Used when adding a new author or moving an author via the author editor', ...authorFolderFormatHelpTexts]}
                  errors={[...authorFolderFormatErrors, ...authorFolderFormatSetting.errors]}
                />
              </FormGroup>

              {
                namingModalOptions &&
                  <NamingModal
                    isOpen={isNamingModalOpen}
                    advancedSettings={advancedSettings}
                    {...namingModalOptions}
                    value={settings[namingModalOptions.name]?.value}
                    onInputChange={onInputChange}
                    onModalClose={this.onNamingModalClose}
                  />
              }

              {
                hasSettings && renameBooks &&
                  <NamingVisualBuilder
                    isOpen={isVisualBuilderOpen}
                    initialPattern={standardBookFormatSetting.value}
                    onSave={this.onVisualBuilderSave}
                    onCancel={this.onVisualBuilderClose}
                  />
              }
            </Form>
        }
      </FieldSet>
    );
  }

}

Naming.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  examples: PropTypes.object.isRequired,
  examplesPopulated: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  mediaManagementSettings: PropTypes.object,
  hasMediaManagementSettings: PropTypes.bool,
  onMediaManagementInputChange: PropTypes.func
};

export default Naming;
