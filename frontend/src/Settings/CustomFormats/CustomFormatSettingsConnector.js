import React, { Component } from 'react';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { icons, kinds } from 'Helpers/Props';
import locationShape from 'Helpers/Props/Shapes/locationShape';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import CustomFormatsConnector from './CustomFormats/CustomFormatsConnector';
import styles from './CustomFormatSettingsConnector.css';

class CustomFormatSettingsConnector extends Component {

  //
  // Render

  render() {
    const fromQuickstart = this.props.location.state?.fromQuickstart === true;

    return (
      <PageContent title={translate('CustomFormatSettings')}>
        <SettingsToolbarConnector showSave={false} />

        <PageContentBody>
          {
            fromQuickstart &&
              <div className={styles.quickstartReturn}>
                <Button
                  kind={kinds.PRIMARY}
                  title={translate('BackToQuickstart')}
                  to="/system/quickstart"
                >
                  <span className={styles.quickstartReturnButtonContent}>
                    <Icon name={icons.ARROW_LEFT} />
                    <span>{translate('BackToQuickstart')}</span>
                  </span>
                </Button>
              </div>
          }

          <DndProvider backend={HTML5Backend}>
            <CustomFormatsConnector />
          </DndProvider>
        </PageContentBody>
      </PageContent>
    );
  }
}

CustomFormatSettingsConnector.propTypes = {
  location: locationShape.isRequired
};

export default CustomFormatSettingsConnector;
