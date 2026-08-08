import PropTypes from 'prop-types';
import React, { createContext, useContext, useEffect, useRef, useState } from 'react';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './Quickstart.css';

const STORAGE_KEY = 'quickstartSectionExpansion';
const QuickstartSectionContext = createContext(null);

function getDefaultExpandedState(sectionKeys, value = true) {
  return sectionKeys.reduce((acc, sectionKey) => {
    acc[sectionKey] = value;
    return acc;
  }, {});
}

function getGuidedActiveSectionKey(guidedSectionKeys, guidedCompletedSectionKeys, fallbackSectionKey) {
  return guidedSectionKeys.find((sectionKey) => !guidedCompletedSectionKeys.includes(sectionKey)) ||
    guidedSectionKeys[guidedSectionKeys.length - 1] ||
    fallbackSectionKey;
}

function getGuidedExpandedState(sectionKeys, activeSectionKey) {
  return sectionKeys.reduce((acc, sectionKey) => {
    acc[sectionKey] = sectionKey === activeSectionKey;
    return acc;
  }, {});
}

function getInitialExpandedState(sectionKeys) {
  const defaultExpandedState = getDefaultExpandedState(sectionKeys);

  try {
    const savedState = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}');

    return {
      ...defaultExpandedState,
      ...Object.keys(savedState).reduce((acc, sectionKey) => {
        if (sectionKeys.includes(sectionKey) && typeof savedState[sectionKey] === 'boolean') {
          acc[sectionKey] = savedState[sectionKey];
        }

        return acc;
      }, {})
    };
  } catch (e) {
    localStorage.removeItem(STORAGE_KEY);
    return defaultExpandedState;
  }
}

function saveExpandedState(expandedState) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(expandedState));
  } catch (e) {
    // Ignore storage errors so Quickstart remains usable in restricted browsers.
  }
}

export function QuickstartSectionProvider(props) {
  const {
    sectionKeys,
    guidedSectionKeys,
    guidedInitialSectionKey,
    guidedReviewSectionKeys,
    guidedCompletedSectionKeys,
    isGuidedMode,
    onGuidedSectionReviewed,
    children
  } = props;

  const guidedActiveSectionKey = getGuidedActiveSectionKey(guidedSectionKeys, guidedCompletedSectionKeys, guidedInitialSectionKey);
  const manuallyToggledSectionKeys = useRef(new Set());

  const [expandedState, setExpandedState] = useState(() => {
    if (isGuidedMode) {
      return getGuidedExpandedState(sectionKeys, guidedActiveSectionKey);
    }

    return getInitialExpandedState(sectionKeys);
  });

  const wasGuidedMode = useRef(isGuidedMode);
  const previousGuidedActiveSectionKey = useRef(guidedActiveSectionKey);

  useEffect(() => {
    const isEnteringGuidedMode = isGuidedMode && !wasGuidedMode.current;
    const activeSectionChanged = isGuidedMode &&
      previousGuidedActiveSectionKey.current !== guidedActiveSectionKey;

    if (isEnteringGuidedMode) {
      manuallyToggledSectionKeys.current.clear();
      setExpandedState(getGuidedExpandedState(sectionKeys, guidedActiveSectionKey));
    } else if (activeSectionChanged) {
      setExpandedState((state) => {
        const newState = {
          ...state
        };

        guidedSectionKeys.forEach((sectionKey) => {
          if (sectionKey === guidedActiveSectionKey) {
            newState[sectionKey] = true;
            return;
          }

          if (!manuallyToggledSectionKeys.current.has(sectionKey)) {
            newState[sectionKey] = false;
          }
        });

        return newState;
      });
    }

    wasGuidedMode.current = isGuidedMode;
    previousGuidedActiveSectionKey.current = guidedActiveSectionKey;
  }, [isGuidedMode, sectionKeys, guidedSectionKeys, guidedActiveSectionKey]);

  const setSectionExpanded = (sectionKey, isExpanded) => {
    if (isGuidedMode) {
      manuallyToggledSectionKeys.current.add(sectionKey);
    }

    setExpandedState((state) => {
      const newState = {
        ...state,
        [sectionKey]: isExpanded
      };

      if (!isGuidedMode) {
        saveExpandedState(newState);
      }

      return newState;
    });
  };

  const setAllExpanded = (isExpanded) => {
    if (isGuidedMode) {
      sectionKeys.forEach((sectionKey) => manuallyToggledSectionKeys.current.add(sectionKey));
    }

    const newState = getDefaultExpandedState(sectionKeys, isExpanded);

    if (!isGuidedMode) {
      saveExpandedState(newState);
    }

    setExpandedState(newState);
  };

  const getNextGuidedSectionKey = (sectionKey) => {
    const sectionIndex = guidedSectionKeys.indexOf(sectionKey);

    if (sectionIndex === -1 || sectionIndex === guidedSectionKeys.length - 1) {
      return null;
    }

    return guidedSectionKeys[sectionIndex + 1];
  };

  const getPreviousGuidedSectionKey = (sectionKey) => {
    const sectionIndex = guidedSectionKeys.indexOf(sectionKey);

    if (sectionIndex <= 0) {
      return null;
    }

    return guidedSectionKeys[sectionIndex - 1];
  };

  const advanceToNextSection = (sectionKey) => {
    if (!isGuidedMode) {
      return;
    }

    const nextSectionKey = getNextGuidedSectionKey(sectionKey);

    if (!nextSectionKey) {
      return;
    }

    if (guidedReviewSectionKeys.includes(sectionKey) && onGuidedSectionReviewed) {
      onGuidedSectionReviewed(sectionKey);
    }

    setExpandedState((state) => ({
      ...state,
      [sectionKey]: false,
      [nextSectionKey]: true
    }));

    window.requestAnimationFrame(() => {
      const nextHeader = document.querySelector(`[data-quickstart-section-header="${nextSectionKey}"]`);

      if (!nextHeader) {
        return;
      }

      nextHeader.scrollIntoView({
        block: 'start',
        behavior: 'smooth'
      });

      try {
        nextHeader.focus({ preventScroll: true });
      } catch (e) {
        nextHeader.focus();
      }
    });
  };

  const allExpanded = sectionKeys.every((sectionKey) => expandedState[sectionKey] !== false);
  const allCollapsed = sectionKeys.every((sectionKey) => expandedState[sectionKey] === false);

  return (
    <QuickstartSectionContext.Provider
      value={{
        expandedState,
        allExpanded,
        allCollapsed,
        isGuidedMode,
        setSectionExpanded,
        setAllExpanded,
        getNextGuidedSectionKey,
        getPreviousGuidedSectionKey,
        advanceToNextSection
      }}
    >
      {children}
    </QuickstartSectionContext.Provider>
  );
}

QuickstartSectionProvider.propTypes = {
  sectionKeys: PropTypes.arrayOf(PropTypes.string).isRequired,
  guidedSectionKeys: PropTypes.arrayOf(PropTypes.string),
  guidedInitialSectionKey: PropTypes.string,
  guidedReviewSectionKeys: PropTypes.arrayOf(PropTypes.string),
  guidedCompletedSectionKeys: PropTypes.arrayOf(PropTypes.string),
  isGuidedMode: PropTypes.bool,
  onGuidedSectionReviewed: PropTypes.func,
  children: PropTypes.node.isRequired
};

QuickstartSectionProvider.defaultProps = {
  guidedSectionKeys: [],
  guidedInitialSectionKey: null,
  guidedReviewSectionKeys: [],
  guidedCompletedSectionKeys: [],
  isGuidedMode: false
};

export function useQuickstartSections() {
  return useContext(QuickstartSectionContext);
}

function QuickstartSection(props) {
  const {
    sectionKey,
    title,
    isComplete,
    children
  } = props;

  const quickstartSections = useQuickstartSections();
  const [localIsExpanded, setLocalIsExpanded] = useState(true);
  const isControlled = !!sectionKey && !!quickstartSections;
  const isExpanded = isControlled ? quickstartSections.expandedState[sectionKey] !== false : localIsExpanded;
  const previousGuidedSectionKey = isControlled ? quickstartSections.getPreviousGuidedSectionKey(sectionKey) : null;
  const showGuidedNext = !isExpanded &&
    quickstartSections?.isGuidedMode &&
    !!previousGuidedSectionKey &&
    quickstartSections.expandedState[previousGuidedSectionKey] !== false;
  const setIsExpanded = (value) => {
    if (isControlled) {
      quickstartSections.setSectionExpanded(sectionKey, value);
      return;
    }

    setLocalIsExpanded(value);
  };

  const onNextPress = () => {
    quickstartSections.advanceToNextSection(previousGuidedSectionKey);
  };

  return (
    <div
      className={styles.section}
      data-quickstart-section={sectionKey}
    >
      <h2 className={styles.sectionHeader}>
        <button
          type="button"
          className={styles.sectionHeaderButton}
          data-quickstart-section-header={sectionKey}
          aria-expanded={isExpanded}
          onClick={() => setIsExpanded(!isExpanded)}
        >
          <span className={styles.sectionHeaderTitle}>
            {title}
            {isComplete && (
              <Icon
                className={styles.checkIcon}
                name={icons.CHECK_CIRCLE}
                kind={kinds.SUCCESS}
              />
            )}
          </span>

          <span className={styles.sectionToggleLabel}>
            <Icon
              name={isExpanded ? icons.COLLAPSE : icons.EXPAND}
              size={13}
            />
            {isExpanded ? translate('Collapse') : translate('Expand')}
          </span>
        </button>

        {showGuidedNext && (
          <Button
            className={styles.quickstartHeaderNextButton}
            kind={kinds.PRIMARY}
            onPress={onNextPress}
          >
            {translate('Next')}
          </Button>
        )}
      </h2>

      <div
        className={isExpanded ? styles.sectionBody : `${styles.sectionBody} ${styles.sectionBodyCollapsed}`}
        aria-hidden={!isExpanded}
      >
        {children}
      </div>
    </div>
  );
}

QuickstartSection.propTypes = {
  sectionKey: PropTypes.string,
  title: PropTypes.node.isRequired,
  isComplete: PropTypes.bool,
  children: PropTypes.node.isRequired
};

QuickstartSection.defaultProps = {
  isComplete: false
};

export default QuickstartSection;
