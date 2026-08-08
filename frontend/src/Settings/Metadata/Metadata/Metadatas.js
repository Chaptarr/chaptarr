import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import PageSectionContent from 'Components/Page/PageSectionContent';
import translate from 'Utilities/String/translate';
import Metadata from './Metadata';
import styles from './Metadatas.css';

function Metadatas(props) {
  const {
    items,
    isFetching,
    error,
    ...otherProps
  } = props;

  if (!isFetching && !error && !items.length) {
    return null;
  }

  return (
    <FieldSet legend={translate('MetadataConsumers')}>
      <PageSectionContent
        errorMessage={translate('UnableToLoadMetadata')}
        isFetching={isFetching}
        error={error}
        {...otherProps}
      >
        <div className={styles.metadatas}>
          {
            items.map((item) => {
              return (
                <Metadata
                  key={item.id}
                  {...item}
                />
              );
            })
          }
        </div>
      </PageSectionContent>
    </FieldSet>
  );
}

Metadatas.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default Metadatas;
