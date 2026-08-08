import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Fragment } from 'react';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import DescriptionListItemDescription from 'Components/DescriptionList/DescriptionListItemDescription';
import DescriptionListItemTitle from 'Components/DescriptionList/DescriptionListItemTitle';
import translate from 'Utilities/String/translate';
import styles from './FileDetails.css';

function renderRejections(rejections) {
  return (
    <span>
      <DescriptionListItemTitle>
        {translate('Rejections')}
      </DescriptionListItemTitle>
      {
        _.map(rejections, (item, key) => {
          return (
            <DescriptionListItemDescription key={key}>
              {item.reason}
            </DescriptionListItemDescription>
          );
        })
      }
    </span>
  );
}

function getTagEntries(tags) {
  if (!tags) {
    return [];
  }

  const entries = Object.entries(tags)
    .map(([key, values]) => {
      const normalizedValues = Array.isArray(values) ? values : [values];

      const filtered = normalizedValues
        .filter((value) => value != null)
        .map((value) => String(value).trim())
        .filter((value) => value.length > 0);

      return [key, filtered];
    })
    .filter(([key, values]) => typeof key === 'string' && key.length > 0 && values.length > 0);

  const colonFree = entries.filter(([key]) => !key.includes(':'));
  const displayEntries = colonFree.length > 0 ? colonFree : entries;

  return displayEntries.sort(([a], [b]) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
}

function FileDetails(props) {

  const {
    filename,
    tags,
    rejections
  } = props;

  const tagEntries = getTagEntries(tags);

  return (
    <Fragment>
      <div className={styles.audioTags}>
        <DescriptionList>
          {
            filename &&
              <DescriptionListItem
                title={translate('Filename')}
                data={filename}
                descriptionClassName={styles.filename}
              />
          }
          {
            tagEntries.length > 0 &&
              tagEntries.map(([key, values]) => {
                return (
                  <DescriptionListItem
                    key={key}
                    title={key}
                    data={values.join(', ')}
                  />
                );
              })
          }
          {
            !!rejections && rejections.length > 0 &&
              renderRejections(rejections)
          }
        </DescriptionList>
      </div>
    </Fragment>
  );
}

FileDetails.propTypes = {
  filename: PropTypes.string,
  tags: PropTypes.object,
  rejections: PropTypes.arrayOf(PropTypes.object)
};

FileDetails.defaultProps = {
  tags: {}
};

export default FileDetails;
