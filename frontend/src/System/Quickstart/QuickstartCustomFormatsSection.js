import PropTypes from 'prop-types';
import React from 'react';
import { withRouter } from 'react-router-dom';
import Button from 'Components/Link/Button';
import { kinds } from 'Helpers/Props';
import getPathWithUrlBase from 'Utilities/getPathWithUrlBase';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import styles from './Quickstart.css';

function QuickstartCustomFormatsSection(props) {
  const {
    history,
    markSectionInteracted,
    quickstartState
  } = props;
  const interactions = quickstartState?.interactions || {};

  const onOpenCustomFormatsPress = () => {
    markSectionInteracted?.({ section: 'customFormats' });
    history.push(
      getPathWithUrlBase('/settings/customformats'),
      { fromQuickstart: true }
    );
  };

  return (
    <QuickstartSection
      sectionKey="customFormats"
      title={translate('QuickstartCustomFormatsTitle')}
      isComplete={!!interactions.customFormats}
    >
      <div className={styles.sectionDescription}>
        {translate('QuickstartCustomFormatsDescription')}
      </div>

      <Button
        kind={kinds.PRIMARY}
        onPress={onOpenCustomFormatsPress}
      >
        {translate('OpenCustomFormats')}
      </Button>
    </QuickstartSection>
  );
}

const historyShape = {
  push: PropTypes.func.isRequired
};

QuickstartCustomFormatsSection.propTypes = {
  history: PropTypes.shape(historyShape).isRequired,
  markSectionInteracted: PropTypes.func,
  quickstartState: PropTypes.object
};

export default withRouter(QuickstartCustomFormatsSection);
