import React from 'react';
import Link from 'Components/Link/Link';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import translate from 'Utilities/String/translate';
import SettingsToolbarConnector from './SettingsToolbarConnector';
import styles from './Settings.css';

function Settings() {
  const sections = [
    {
      title: 'Library',
      items: [
        [
          'MediaManagement',
          '/settings/mediamanagement',
          'MediaManagementSettingsSummary'
        ],
        ['Conversion', '/settings/conversion', 'ConversionSettingsSummary'],
        ['Profiles', '/settings/profiles', 'ProfilesSettingsSummary'],
        [
          'CustomFormats',
          '/settings/customformats',
          'CustomFormatsAndSettings'
        ],
        ['Tags', '/settings/tags', 'TagsSettingsSummary']
      ]
    },
    {
      title: 'Search',
      items: [
        ['Metadata', '/settings/metadata', 'MetadataSettingsSummary'],
        ['Indexers', '/settings/indexers', 'IndexersSettingsSummary'],
        ['ImportLists', '/settings/importlists', 'ListsSettingsSummary']
      ]
    },
    {
      title: 'Connect',
      items: [
        [
          'DownloadClients',
          '/settings/downloadclients',
          'DownloadClientsSettingsSummary'
        ],
        ['Connect', '/settings/connect', 'ConnectSettingsSummary']
      ]
    },
    {
      title: 'General',
      items: [
        ['General', '/settings/general', 'GeneralSettingsSummary'],
        ['Ui', '/settings/ui', 'UISettingsSummary']
      ]
    },
    {
      title: 'System',
      items: [
        ['Quickstart', '/system/quickstart', 'QuickstartReviewDescription']
      ]
    }
  ];

  return (
    <PageContent title={translate('Settings')}>
      <SettingsToolbarConnector hasPendingChanges={false} />

      <PageContentBody>
        <div className={styles.sections}>
          {sections.map((section) => (
            <section key={section.title} className={styles.section}>
              <h2 className={styles.sectionTitle}>
                {translate(section.title)}
              </h2>
              {section.items.map(([title, to, summary]) => (
                <div key={to} className={styles.sectionItem}>
                  <Link className={styles.link} to={to}>
                    {translate(title)}
                  </Link>
                  <div className={styles.summary}>{translate(summary)}</div>
                </div>
              ))}
            </section>
          ))}
        </div>
      </PageContentBody>
    </PageContent>
  );
}

Settings.propTypes = {};

export default Settings;
