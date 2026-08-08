import React from 'react';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './PathPreview.css';

function PathPreview({ preview, isLoading }) {
  return (
    <div className={styles.preview}>
      <div className={styles.previewHeader}>
        <Icon name={icons.FILE} size={16} />
        <span>{translate('NamingBuilderLivePreview')}</span>
        {isLoading && <LoadingIndicator className={styles.loader} size={16} />}
      </div>

      <div className={styles.previewPath}>
        {preview.path || translate('NamingBuilderNoPreviewAvailable')}

        {preview.path && (
          <span className={styles.extension}>{'.mp3'}</span>
        )}
      </div>

      {preview.segments && preview.segments.length > 1 && (
        <div className={styles.segments}>
          {preview.segments.map((segment, index) => (
            <span
              key={index}
              className={`${styles.segment} ${segment.nodeId ? styles.highlighted : ''}`}
              data-node-id={segment.nodeId}
            >
              {segment.text}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

export default PathPreview;
