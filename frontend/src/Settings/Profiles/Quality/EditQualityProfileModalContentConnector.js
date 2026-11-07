import cloneDeep from 'lodash/cloneDeep';
import find from 'lodash/find';
import isEmpty from 'lodash/isEmpty';
import reduceRight from 'lodash/reduceRight';
import some from 'lodash/some';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchQualityProfileSchema, saveQualityProfile, setQualityProfileValue, toggleAdvancedSettings } from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import translate from 'Utilities/String/translate';
import { applyEasyCustomFormatPreset } from './dramatizedAudioPreference';
import EditQualityProfileModalContent from './EditQualityProfileModalContent';

function parseIndex(index) {
  const split = index.split('.');

  if (split.length === 1) {
    return [
      null,
      parseInt(split[0]) - 1
    ];
  }

  return [
    parseInt(split[0]) - 1,
    parseInt(split[1]) - 1
  ];
}

function createQualitiesSelector() {
  return createSelector(
    createProviderSettingsSelector('qualityProfiles'),
    (qualityProfile) => {
      const items = qualityProfile.item.items;
      if (!items || !items.value) {
        return [];
      }

      return reduceRight(items.value, (result, { allowed, id, name, quality }) => {
        if (allowed) {
          if (id) {
            result.push({
              key: id,
              value: name,
              isConversionTarget: false
            });
          } else {
            result.push({
              key: quality.id,
              value: quality.name,
              isConversionTarget: !!quality.isConversionTarget
            });
          }
        }

        return result;
      }, []);
    }
  );
}

function createConvertToQualitiesSelector() {
  return createSelector(
    createQualitiesSelector(),
    (qualities) => {
      return [
        {
          key: 0,
          value: translate('DoNotConvert'),
          isConversionTarget: false
        },
        ...qualities.filter((quality) => quality.isConversionTarget)
      ];
    }
  );
}

function createQualityProfileInUseSelector() {
  return createSelector(
    (state, { id }) => id,
    (state) => state.authors?.items || [],
    (state) => state.settings.importLists?.items || [],
    (state) => state.settings.rootFolders?.items || [],
    (id, authors = [], lists = [], rootFolders = []) => {
      if (!id) {
        return false;
      }

      const matchesQualityProfile = (item) => {
        return item?.qualityProfileId === id ||
          item?.audiobookQualityProfileId === id ||
          item?.ebookQualityProfileId === id ||
          item?.audiobook?.qualityProfileId === id ||
          item?.ebook?.qualityProfileId === id;
      };

      return some(authors, matchesQualityProfile) ||
        some(lists, matchesQualityProfile) ||
        some(rootFolders, matchesQualityProfile);
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createProviderSettingsSelector('qualityProfiles'),
    createQualitiesSelector(),
    createConvertToQualitiesSelector(),
    createQualityProfileInUseSelector(),
    (advancedSettings, qualityProfile, qualities, convertToQualities, isInUse) => {
      return {
        advancedSettings,
        qualities,
        convertToQualities,
        ...qualityProfile,
        isInUse
      };
    }
  );
}

const mapDispatchToProps = {
  fetchQualityProfileSchema,
  setQualityProfileValue,
  saveQualityProfile,
  toggleAdvancedSettings
};

class EditQualityProfileModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      dragQualityIndex: null,
      dropQualityIndex: null,
      dropPosition: null,
      editGroups: false
    };
  }

  componentDidMount() {
    if (!this.props.id && (this.props.profileType || !this.props.isPopulated)) {
      this.props.fetchQualityProfileSchema({ profileType: this.props.profileType });
    }
  }

  componentDidUpdate(prevProps, prevState) {
    if (!this.props.id && this.props.profileType && this.props.profileType !== prevProps.profileType) {
      this.props.fetchQualityProfileSchema({ profileType: this.props.profileType });
    }

    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }
  }

  //
  // Control

  ensureCutoff = (qualityProfile) => {
    const cutoff = qualityProfile.cutoff.value;

    const cutoffItem = find(qualityProfile.items.value, (i) => {
      if (!cutoff) {
        return false;
      }

      return i.id === cutoff || (i.quality && i.quality.id === cutoff);
    });

    // If the cutoff isn't allowed anymore or there isn't a cutoff set one
    if (!cutoff || !cutoffItem || !cutoffItem.allowed) {
      const firstAllowed = find(qualityProfile.items.value, { allowed: true });
      let cutoffId = null;

      if (firstAllowed) {
        cutoffId = firstAllowed.quality ? firstAllowed.quality.id : firstAllowed.id;
      }

      this.props.setQualityProfileValue({ name: 'cutoff', value: cutoffId });
    }
  };

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setQualityProfileValue({ name, value });
  };

  onCutoffChange = ({ name, value }) => {
    const id = parseInt(value);
    const item = find(this.props.item.items.value, (i) => {
      if (i.quality) {
        return i.quality.id === id;
      }

      return i.id === id;
    });

    const cutoffId = item.quality ? item.quality.id : item.id;

    this.props.setQualityProfileValue({ name, value: cutoffId });
  };

  onConvertToQualityChange = ({ name, value }) => {
    const id = parseInt(value);
    const convertToQualityId = id > 0 ? id : 0;

    this.props.setQualityProfileValue({ name, value: convertToQualityId });
  };

  onReleasePriorityChange = ({ value }) => {
    this.props.setQualityProfileValue({
      name: 'preferCustomFormatsOverQuality',
      value: value === 'preferences'
    });
  };

  onEasyCustomFormatPresetChange = ({ value }) => {
    if (!value) {
      return;
    }

    const qualityProfile = cloneDeep(this.props.item);
    const result = applyEasyCustomFormatPreset(
      qualityProfile.formatItems.value,
      value,
      qualityProfile.minFormatScore.value
    );

    this.props.setQualityProfileValue({
      name: 'formatItems',
      value: result.formatItems
    });

    if (result.minFormatScore !== qualityProfile.minFormatScore.value) {
      this.props.setQualityProfileValue({
        name: 'minFormatScore',
        value: result.minFormatScore
      });
    }
  };

  onSavePress = () => {
    this.props.saveQualityProfile({ id: this.props.id });
  };

  onAdvancedSettingsPress = () => {
    this.props.toggleAdvancedSettings();
  };

  onQualityProfileItemAllowedChange = (id, allowed) => {
    const qualityProfile = cloneDeep(this.props.item);
    const items = qualityProfile.items.value;
    const item = find(qualityProfile.items.value, (i) => i.quality && i.quality.id === id);

    item.allowed = allowed;

    this.props.setQualityProfileValue({
      name: 'items',
      value: items
    });

    if (id === this.props.item.convertToQualityId?.value && !allowed) {
      this.props.setQualityProfileValue({ name: 'convertToQualityId', value: 0 });
      this.props.setQualityProfileValue({ name: 'convertMp3ToM4b', value: false });
    }

    this.ensureCutoff(qualityProfile);
  };

  onQualityProfileFormatItemScoreChange = (id, score) => {
    const qualityProfile = cloneDeep(this.props.item);
    const formatItems = qualityProfile.formatItems.value;
    const item = find(qualityProfile.formatItems.value, (i) => i.format === id);

    item.score = score;

    this.props.setQualityProfileValue({
      name: 'formatItems',
      value: formatItems
    });
  };

  onQualityProfileItemDragMove = (options) => {
    const {
      dragQualityIndex,
      dropQualityIndex,
      dropPosition
    } = options;

    const [dragGroupIndex, dragItemIndex] = parseIndex(dragQualityIndex);
    const [dropGroupIndex, dropItemIndex] = parseIndex(dropQualityIndex);

    if (
      (dropPosition === 'below' && dropItemIndex - 1 === dragItemIndex) ||
      (dropPosition === 'above' && dropItemIndex + 1 === dragItemIndex)
    ) {
      if (
        this.state.dragQualityIndex != null &&
        this.state.dropQualityIndex != null &&
        this.state.dropPosition != null
      ) {
        this.setState({
          dragQualityIndex: null,
          dropQualityIndex: null,
          dropPosition: null
        });
      }

      return;
    }

    let adjustedDropQualityIndex = dropQualityIndex;

    // Correct dragging out of a group to the position above
    if (
      dropPosition === 'above' &&
      dragGroupIndex !== dropGroupIndex &&
      dropGroupIndex != null
    ) {
      // Add 1 to the group index and 2 to the item index so it's inserted above in the correct group
      adjustedDropQualityIndex = `${dropGroupIndex + 1}.${dropItemIndex + 2}`;
    }

    // Correct inserting above outside a group
    if (
      dropPosition === 'above' &&
      dragGroupIndex !== dropGroupIndex &&
      dropGroupIndex == null
    ) {
      // Add 2 to the item index so it's entered in the correct place
      adjustedDropQualityIndex = `${dropItemIndex + 2}`;
    }

    // Correct inserting below a quality within the same group (when moving a lower item)
    if (
      dropPosition === 'below' &&
      dragGroupIndex === dropGroupIndex &&
      dropGroupIndex != null &&
      dragItemIndex < dropItemIndex
    ) {
      // Add 1 to the group index leave the item index
      adjustedDropQualityIndex = `${dropGroupIndex + 1}.${dropItemIndex}`;
    }

    // Correct inserting below a quality outside a group (when moving a lower item)
    if (
      dropPosition === 'below' &&
      dragGroupIndex === dropGroupIndex &&
      dropGroupIndex == null &&
      dragItemIndex < dropItemIndex
    ) {
      // Leave the item index so it's inserted below the item
      adjustedDropQualityIndex = `${dropItemIndex}`;
    }

    if (
      dragQualityIndex !== this.state.dragQualityIndex ||
      adjustedDropQualityIndex !== this.state.dropQualityIndex ||
      dropPosition !== this.state.dropPosition
    ) {
      this.setState({
        dragQualityIndex,
        dropQualityIndex: adjustedDropQualityIndex,
        dropPosition
      });
    }
  };

  onQualityProfileItemDragEnd = (didDrop) => {
    const {
      dragQualityIndex,
      dropQualityIndex
    } = this.state;

    if (didDrop && dropQualityIndex != null) {
      const qualityProfile = cloneDeep(this.props.item);
      const items = qualityProfile.items.value;
      const [dragGroupIndex, dragItemIndex] = parseIndex(dragQualityIndex);
      const [dropGroupIndex, dropItemIndex] = parseIndex(dropQualityIndex);

      let item = null;
      let dropGroup = null;

      if (dropGroupIndex != null) {
        dropGroup = items[dropGroupIndex];
      }

      if (dragGroupIndex == null) {
        item = items.splice(dragItemIndex, 1)[0];
      } else {
        const group = items[dragGroupIndex];
        item = group.items.splice(dragItemIndex, 1)[0];

        // If the group is now empty, destroy it.
        if (!group.items.length) {
          items.splice(dragGroupIndex, 1);
        }
      }

      if (dropGroupIndex == null) {
        items.splice(dropItemIndex, 0, item);
      } else {
        dropGroup.items.splice(dropItemIndex, 0, item);
      }

      this.props.setQualityProfileValue({
        name: 'items',
        value: items
      });

      this.ensureCutoff(qualityProfile);
    }

    this.setState({
      dragQualityIndex: null,
      dropQualityIndex: null,
      dropPosition: null
    });
  };

  //
  // Render

  render() {
    if (isEmpty(this.props.item.items) && !this.props.isFetching) {
      return null;
    }

    return (
      <EditQualityProfileModalContent
        {...this.state}
        {...this.props}
        onSavePress={this.onSavePress}
        onInputChange={this.onInputChange}
        onCutoffChange={this.onCutoffChange}
        onConvertToQualityChange={this.onConvertToQualityChange}
        onReleasePriorityChange={this.onReleasePriorityChange}
        onEasyCustomFormatPresetChange={this.onEasyCustomFormatPresetChange}
        onAdvancedSettingsPress={this.onAdvancedSettingsPress}
        onQualityProfileItemAllowedChange={this.onQualityProfileItemAllowedChange}
        onQualityProfileItemDragMove={this.onQualityProfileItemDragMove}
        onQualityProfileItemDragEnd={this.onQualityProfileItemDragEnd}
        onQualityProfileFormatItemScoreChange={this.onQualityProfileFormatItemScoreChange}
      />
    );
  }
}

EditQualityProfileModalContentConnector.propTypes = {
  id: PropTypes.number,
  profileType: PropTypes.string,
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  item: PropTypes.object.isRequired,
  convertToQualities: PropTypes.arrayOf(PropTypes.object).isRequired,
  setQualityProfileValue: PropTypes.func.isRequired,
  fetchQualityProfileSchema: PropTypes.func.isRequired,
  saveQualityProfile: PropTypes.func.isRequired,
  toggleAdvancedSettings: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditQualityProfileModalContentConnector);
