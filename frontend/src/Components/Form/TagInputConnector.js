import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { addTag } from 'Store/Actions/tagActions';
import createTagsSelector from 'Store/Selectors/createTagsSelector';
import TagInput from './TagInput';

const validTagRegex = new RegExp('[^-_a-z0-9]', 'i');

function isValidTag(tagName) {
  try {
    return !validTagRegex.test(tagName);
  } catch (e) {
    return false;
  }
}

function createMapStateToProps() {
  return createSelector(
    (state, { value }) => value,
    createTagsSelector(),
    (tags = [], tagList) => {
      const sortedTags = _.sortBy(tagList, 'label');
      const filteredTagList = _.filter(sortedTags, (tag) => _.indexOf(tags, tag.id) === -1);

      return {
        tags: tags.reduce((acc, tag) => {
          const matchingTag = _.find(tagList, { id: tag });

          if (matchingTag) {
            acc.push({
              id: tag,
              name: matchingTag.label
            });
          }

          return acc;
        }, []),

        tagList: filteredTagList.map(({ id, label: name }) => {
          return {
            id,
            name
          };
        }),

        allTags: sortedTags
      };
    }
  );
}

const mapDispatchToProps = {
  addTag
};

class TagInputConnector extends Component {

  //
  // Listeners

  onTagAdd = (tag) => {
    const {
      name,
      value,
      allTags
    } = this.props;

    if (!tag.id) {
      const normalizedName = (tag.name || '').trim().toLowerCase();
      const existingTag = _.find(allTags, (t) => (t.label || '').trim().toLowerCase() === normalizedName);

      if (existingTag) {
        const currentValue = value || [];
        if (!currentValue.includes(existingTag.id)) {
          this.props.onChange({ name, value: currentValue.concat(existingTag.id) });
        }

        return;
      }

      if (isValidTag(tag.name)) {
        this.props.addTag({
          tag: { label: tag.name },
          onTagCreated: this.onTagCreated
        });
      }

      return;
    }

    const currentValue = value || [];
    if (currentValue.includes(tag.id)) {
      return;
    }

    const newValue = currentValue.slice();
    newValue.push(tag.id);

    this.props.onChange({ name, value: newValue });
  };

  onTagDelete = ({ index, id }) => {
    const {
      name,
      value
    } = this.props;

    const currentValue = value || [];
    const newValue = id != null
      ? currentValue.filter((tagId) => tagId !== id)
      : currentValue.slice();

    if (id == null) {
      newValue.splice(index, 1);
    }

    this.props.onChange({
      name,
      value: newValue
    });
  };

  onTagCreated = (tag) => {
    const {
      name,
      value
    } = this.props;

    const currentValue = value || [];
    if (currentValue.includes(tag.id)) {
      return;
    }

    const newValue = currentValue.slice();
    newValue.push(tag.id);

    this.props.onChange({ name, value: newValue });
  };

  //
  // Render

  render() {
    // Provider field tag selects may pass a `values` prop; TagInput doesn't use it.
    // Avoid leaking unknown props down to AutoSuggestInput.
    const { values, ...otherProps } = this.props;

    return (
      <TagInput
        onTagAdd={this.onTagAdd}
        onTagDelete={this.onTagDelete}
        onTagReplace={this.onTagReplace}
        {...otherProps}
      />
    );
  }
}

TagInputConnector.propTypes = {
  name: PropTypes.string.isRequired,
  value: PropTypes.arrayOf(PropTypes.number).isRequired,
  tags: PropTypes.arrayOf(PropTypes.object).isRequired,
  allTags: PropTypes.arrayOf(PropTypes.object).isRequired,
  onChange: PropTypes.func.isRequired,
  addTag: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TagInputConnector);
