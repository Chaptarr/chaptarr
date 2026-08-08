import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputHelpText from 'Components/Form/FormInputHelpText';
import FormLabel from 'Components/Form/FormLabel';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import Measure from 'Components/Measure';
import { icons, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import QualityProfileItemDragPreview from './QualityProfileItemDragPreview';
import QualityProfileItemDragSource from './QualityProfileItemDragSource';
import styles from './QualityProfileItems.css';

class QualityProfileItems extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      qualitiesHeight: 0,
      qualitiesHeightEditGroups: 0
    };
  }

  //
  // Listeners

  onMeasure = ({ height }) => {
    if (this.props.editGroups) {
      this.setState({
        qualitiesHeightEditGroups: height
      });
    } else {
      this.setState({ qualitiesHeight: height });
    }
  };

  onToggleEditGroupsMode = () => {
    this.props.onToggleEditGroupsMode();
  };

  //
  // Render

  render() {
    const {
      editGroups,
      dropQualityIndex,
      dropPosition,
      qualityProfileItems,
      errors,
      warnings,
      ...otherProps
    } = this.props;

    const {
      qualitiesHeight,
      qualitiesHeightEditGroups
    } = this.state;

    const isDragging = dropQualityIndex !== null;
    const isDraggingUp = isDragging && dropPosition === 'above';
    const isDraggingDown = isDragging && dropPosition === 'below';
    const minHeight = editGroups ? qualitiesHeightEditGroups : qualitiesHeight;

    return (
      <FormGroup size={sizes.EXTRA_SMALL}>
        <FormLabel size={sizes.SMALL}>
          {translate('Qualities')}
        </FormLabel>

        <div>
          <FormInputHelpText
            text="Qualities higher in the list are more preferred."
          />

          {
            errors.map((error, index) => {
              return (
                <FormInputHelpText
                  key={index}
                  text={error.message}
                  isError={true}
                  isCheckInput={false}
                />
              );
            })
          }

          {
            warnings.map((warning, index) => {
              return (
                <FormInputHelpText
                  key={index}
                  text={warning.message}
                  isWarning={true}
                  isCheckInput={false}
                />
              );
            })
          }


          <Measure
            includeMargin={false}
            onMeasure={this.onMeasure}
            className={styles.qualities}
            style={{ minHeight: `${minHeight}px` }}
          >
            {
              // Readarr pattern: build items in storage order with correct indices, then reverse for display
              qualityProfileItems
                .map(({ id, name, allowed, quality, items }, index) => {
                  const identifier = quality ? quality.id : id;

                  return (
                    <QualityProfileItemDragSource
                      key={identifier}
                      editGroups={editGroups}
                      groupId={id}
                      qualityId={quality && quality.id}
                      name={quality ? quality.name : name}
                      allowed={allowed}
                      items={items}
                      qualityIndex={`${index + 1}`}
                      isInGroup={false}
                      isDragging={isDragging}
                      isDraggingUp={isDraggingUp}
                      isDraggingDown={isDraggingDown}
                      {...otherProps}
                    />
                  );
                })
                .reverse()
            }

            <QualityProfileItemDragPreview />
          </Measure>
        </div>
      </FormGroup>
    );
  }
}

QualityProfileItems.propTypes = {
  editGroups: PropTypes.bool.isRequired,
  dragQualityIndex: PropTypes.string,
  dropQualityIndex: PropTypes.string,
  dropPosition: PropTypes.string,
  qualityProfileItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  errors: PropTypes.arrayOf(PropTypes.object),
  warnings: PropTypes.arrayOf(PropTypes.object),
  onToggleEditGroupsMode: PropTypes.func
};

QualityProfileItems.defaultProps = {
  errors: [],
  warnings: []
};

export default QualityProfileItems;
