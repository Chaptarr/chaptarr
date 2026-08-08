import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './AddRootFolderInline.css';

class AddRootFolderInline extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isExpanded: false,
      selectedPath: '',
      qualityProfileId: null
    };
  }

  //
  // Listeners

  onExpandPress = () => {
    this.setState({
      isExpanded: true
    });
  };

  onCancelPress = () => {
    this.setState({
      isExpanded: false,
      selectedPath: '',
      qualityProfileId: null
    });
  };

  onPathChange = ({ value }) => {
    this.setState({ selectedPath: value });
  };

  onQualityProfileChange = ({ value }) => {
    this.setState({ qualityProfileId: value });
  };

  onAddPress = () => {
    const { selectedPath, qualityProfileId } = this.state;
    const { folderType, onAddRootFolder } = this.props;

    if (selectedPath) {
      const mediaTypeField = folderType === 1 ? 'audiobookQualityProfileId' : 'ebookQualityProfileId';
      onAddRootFolder({
        path: selectedPath,
        folderType,
        [mediaTypeField]: qualityProfileId
      });
    }
  };

  //
  // Render

  render() {
    const {
      folderType
    } = this.props;

    const {
      isExpanded,
      selectedPath,
      qualityProfileId
    } = this.state;

    const mediaTypeName = folderType === 1 ? 'audiobook' : 'ebook';
    const MediaTypeName = folderType === 1 ? 'Audiobook' : 'Ebook';

    if (!isExpanded) {
      return (
        <Button
          kind={kinds.PRIMARY}
          onPress={this.onExpandPress}
        >
          <Icon name={icons.FOLDER_OPEN} />
          {` Add ${mediaTypeName} root folder`}
        </Button>
      );
    }

    return (
      <div className={styles.inlineForm}>
        <div className={styles.header}>
          <Icon name={icons.INFO} className={styles.infoIcon} />
          <div className={styles.infoText}>
            {translate('AddRootFolderInlineIntroPrefix')} <strong>{translate('AddRootFolderInlineIntroAllMediaType', { mediaTypeName })}</strong> {translate('AddRootFolderInlineIntroSuffix')}
          </div>
        </div>

        <Form>
          <FormGroup>
            <FormLabel>{translate('AddRootFolderInlineRootFolderLabel', { mediaType: MediaTypeName })}</FormLabel>
            <FormInputGroup
              type={inputTypes.PATH}
              name="path"
              value={selectedPath}
              propagateValueOnChange={true}
              onChange={this.onPathChange}
              helpText={`Path where all ${mediaTypeName}s will be stored`}
            />
          </FormGroup>

          <FormGroup>
            <FormLabel>{translate('QualityProfile')}</FormLabel>
            <FormInputGroup
              type={inputTypes.QUALITY_PROFILE_SELECT}
              name="qualityProfileId"
              value={qualityProfileId}
              profileType={folderType === 1 ? 'audiobook' : 'ebook'}
              onChange={this.onQualityProfileChange}
              helpText={`Quality profile for new ${mediaTypeName}s in this folder`}
            />
          </FormGroup>

          <div className={styles.buttons}>
            <Button
              onPress={this.onCancelPress}
            >
              {translate('Cancel')}
            </Button>

            <Button
              kind={kinds.PRIMARY}
              isDisabled={!selectedPath}
              onPress={this.onAddPress}
            >
              {translate('AddRootFolder')}
            </Button>
          </div>
        </Form>
      </div>
    );
  }
}

AddRootFolderInline.propTypes = {
  folderType: PropTypes.number.isRequired,
  onAddRootFolder: PropTypes.func.isRequired
};

export default AddRootFolderInline;
