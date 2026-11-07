import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import QuickstartSection, { useQuickstartSections } from './QuickstartSection';
import styles from './QuickstartSetup.css';

const mediaScopeOptions = [
  {
    value: 'audiobook',
    label: 'Audiobooks',
    icon: '🎧'
  },
  {
    value: 'both',
    label: 'Both',
    icon: null
  },
  {
    value: 'ebook',
    label: 'Ebooks',
    icon: '📖'
  }
];

function QuickstartMediaScopeSection(props) {
  const {
    mediaScope,
    onMediaScopeChange
  } = props;

  const {
    isGuidedMode,
    advanceToNextSection
  } = useQuickstartSections();

  const onMediaScopePress = (value) => {
    onMediaScopeChange(value);

    if (isGuidedMode) {
      advanceToNextSection('mediaScope');
    }
  };

  return (
    <QuickstartSection
      sectionKey="mediaScope"
      title={translate('QuickstartMediaScopeTitle')}
      isComplete={!!mediaScope}
    >
      <div className={styles.sectionDescription}>
        {translate('QuickstartMediaScopeDescription')}
      </div>

      <div
        className={styles.mediaScopeOptions}
        role="group"
        aria-label={translate('QuickstartMediaScopeTitle')}
      >
        {mediaScopeOptions.map((option) => {
          const isSelected = mediaScope === option.value;

          return (
            <button
              key={option.value}
              type="button"
              className={`${styles.mediaScopeOption} ${isSelected ? styles.mediaScopeOptionSelected : ''}`}
              aria-pressed={isSelected}
              onClick={() => onMediaScopePress(option.value)}
            >
              {option.icon && <span aria-hidden="true">{option.icon}</span>}
              {translate(option.label)}
            </button>
          );
        })}
      </div>
    </QuickstartSection>
  );
}

QuickstartMediaScopeSection.propTypes = {
  mediaScope: PropTypes.oneOf(['audiobook', 'ebook', 'both']),
  onMediaScopeChange: PropTypes.func.isRequired
};

export default QuickstartMediaScopeSection;
