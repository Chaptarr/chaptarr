import React, { useState } from 'react';
import Button from 'Components/Link/Button';
import { kinds } from 'Helpers/Props';
import SettingsBackupModal from 'System/SettingsBackup/SettingsBackupModal';
import SettingsRestoreModal from 'System/SettingsBackup/SettingsRestoreModal';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import styles from './Quickstart.css';

function QuickstartSettingsBackupSection() {
  const [isBackupOpen, setIsBackupOpen] = useState(false);
  const [isRestoreOpen, setIsRestoreOpen] = useState(false);

  return (
    <QuickstartSection
      sectionKey="settingsBackup"
      title={translate('QuickstartSettingsBackupTitle')}
    >
      <div className={styles.sectionDescription}>
        {translate('QuickstartSettingsBackupDescription')}
      </div>

      <div className={`${styles.quickstartCardActions} ${styles.cardActionsWrap}`}>
        <Button
          kind={kinds.PRIMARY}
          onPress={() => setIsBackupOpen(true)}
        >
          {translate('SettingsBackupMySettings')}
        </Button>

        <Button
          kind={kinds.DEFAULT}
          onPress={() => setIsRestoreOpen(true)}
        >
          {translate('SettingsRestoreMySettings')}
        </Button>
      </div>

      <SettingsBackupModal
        isOpen={isBackupOpen}
        onModalClose={() => setIsBackupOpen(false)}
      />

      <SettingsRestoreModal
        isOpen={isRestoreOpen}
        onModalClose={() => setIsRestoreOpen(false)}
      />
    </QuickstartSection>
  );
}

export default QuickstartSettingsBackupSection;
