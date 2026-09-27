import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import split from 'Utilities/String/split';
import TagInput from './TagInput';

function createMapStateToProps() {
  return createSelector(
    (state, { value }) => value,
    (state) => state.settings.metadataProfiles.availableGenres,
    (tags, availableGenres) => {
      const tagsArray = Array.isArray(tags) ? tags : split(tags);

      return {
        tags: tagsArray.reduce((result, tag) => {
          if (tag) {
            result.push({
              id: tag,
              name: tag
            });
          }

          return result;
        }, []),
        tagList: (availableGenres || []).map((genre) => {
          return {
            id: genre,
            name: genre
          };
        }),
        valueArray: tagsArray
      };
    }
  );
}

// Same tag-splitting/free-text behavior as TextTagInputConnector, but backed by a real
// tagList of genres already seen in the library (fetched via fetchMetadataProfileGenres),
// so typing offers autosuggest instead of the plain free-text-only field.
class GenreTagInputConnector extends Component {

  //
  // Listeners

  onTagAdd = (tag) => {
    const {
      name,
      valueArray,
      onChange
    } = this.props;

    const newValue = [...valueArray];
    const newTags = tag.name.startsWith('/') ? [tag.name] : split(tag.name);

    newTags.forEach((newTag) => {
      newValue.push(newTag.trim());
    });

    onChange({ name, value: newValue });
  };

  onTagDelete = ({ index }) => {
    const {
      name,
      valueArray,
      onChange
    } = this.props;

    const newValue = [...valueArray];
    newValue.splice(index, 1);

    onChange({
      name,
      value: newValue
    });
  };

  onTagReplace = (tagToReplace, newTag) => {
    const {
      name,
      valueArray,
      onChange
    } = this.props;

    const newValue = [...valueArray];
    newValue.splice(tagToReplace.index, 1);
    newValue.push(newTag.name.trim());

    onChange({ name, value: newValue });
  };

  //
  // Render

  render() {
    return (
      <TagInput
        delimiters={['Tab', 'Enter', ',']}
        allowNew={true}
        onTagAdd={this.onTagAdd}
        onTagDelete={this.onTagDelete}
        onTagReplace={this.onTagReplace}
        {...this.props}
      />
    );
  }
}

GenreTagInputConnector.propTypes = {
  name: PropTypes.string.isRequired,
  valueArray: PropTypes.arrayOf(PropTypes.string).isRequired,
  onChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, null)(GenreTagInputConnector);
