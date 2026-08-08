import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import { kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './SettingsBackupModal.css';

export const settingsBackupCategoryDefinitions = [
  {
    key: 'indexers',
    get label() {
      return translate('Indexers');
    },
    get description() {
      return translate('SettingsBackupIndexersDescription');
    }
  },
  {
    key: 'downloadClients',
    get label() {
      return translate('DownloadClients');
    },
    get description() {
      return translate('SettingsBackupDownloadClientsDescription');
    }
  },
  {
    key: 'remotePathMappings',
    get label() {
      return translate('RemotePathMappings');
    },
    get description() {
      return translate('SettingsBackupRemotePathMappingsDescription');
    }
  },
  {
    key: 'connections',
    get label() {
      return translate('Connections');
    },
    get description() {
      return translate('SettingsBackupConnectionsDescription');
    }
  },
  {
    key: 'proxies',
    get label() {
      return translate('SettingsBackupProxiesLabel');
    },
    get description() {
      return translate('SettingsBackupProxiesDescription');
    }
  },
  {
    key: 'hardcover',
    get label() {
      return translate('Hardcover');
    },
    get description() {
      return translate('SettingsBackupHardcoverDescription');
    }
  },
  {
    key: 'profiles',
    get label() {
      return translate('Profiles');
    },
    get description() {
      return translate('SettingsBackupProfilesDescription');
    }
  },
  {
    key: 'mediaManagement',
    get label() {
      return translate('SettingsBackupMediaManagementLabel');
    },
    get description() {
      return translate('SettingsBackupMediaManagementDescription');
    }
  },
  {
    key: 'metadataServer',
    get label() {
      return translate('SettingsBackupMetadataServerLabel');
    },
    get description() {
      return translate('SettingsBackupMetadataServerDescription');
    }
  }
];

export function getDefaultSettingsBackupCategories() {
  return settingsBackupCategoryDefinitions.reduce((result, category) => {
    result[category.key] = true;
    return result;
  }, {});
}

export function toCategoryList(categories) {
  return settingsBackupCategoryDefinitions
    .filter((category) => categories[category.key])
    .map((category) => category.key);
}

function SettingsBackupCategoryPicker(props) {
  const {
    categories,
    onToggleCategory,
    onSelectAll,
    onSelectNone
  } = props;

  const selectedCount = toCategoryList(categories).length;

  return (
    <div className={styles.categoryPicker}>
      <div className={styles.categoryPickerHeader}>
        <div>
          <div className={styles.categoryPickerTitle}>
            {translate('Categories')}
          </div>

          <div className={styles.categoryPickerCount}>
            {translate('SettingsBackupCategoriesSelected', { selected: selectedCount, total: settingsBackupCategoryDefinitions.length })}
          </div>
        </div>

        <div className={styles.categoryPickerActions}>
          <Button
            kind={kinds.DEFAULT}
            size={sizes.SMALL}
            onPress={onSelectAll}
          >
            {translate('SelectAll')}
          </Button>

          <Button
            kind={kinds.DEFAULT}
            size={sizes.SMALL}
            onPress={onSelectNone}
          >
            {translate('SelectNone')}
          </Button>
        </div>
      </div>

      <div className={styles.categoryGrid}>
        {
          settingsBackupCategoryDefinitions.map((category) => {
            const isChecked = !!categories[category.key];

            return (
              <label
                key={category.key}
                className={styles.categoryOption}
              >
                <input
                  className={styles.categoryInput}
                  type="checkbox"
                  checked={isChecked}
                  onChange={() => onToggleCategory(category.key)}
                />

                <span className={styles.categoryCheck}>
                  {isChecked ? '✓' : ''}
                </span>

                <span className={styles.categoryText}>
                  <span className={styles.categoryName}>
                    {category.label}
                  </span>

                  <span className={styles.categoryDescription}>
                    {category.description}
                  </span>
                </span>
              </label>
            );
          })
        }
      </div>
    </div>
  );
}

SettingsBackupCategoryPicker.propTypes = {
  categories: PropTypes.object.isRequired,
  onToggleCategory: PropTypes.func.isRequired,
  onSelectAll: PropTypes.func.isRequired,
  onSelectNone: PropTypes.func.isRequired
};

export default SettingsBackupCategoryPicker;
