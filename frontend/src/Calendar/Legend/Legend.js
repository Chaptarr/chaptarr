import PropTypes from 'prop-types';
import React from 'react';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import LegendIconItem from './LegendIconItem';
import LegendItem from './LegendItem';
import styles from './Legend.css';

function Legend(props) {
  const {
    showCutoffUnmetIcon,
    colorImpairedMode
  } = props;

  const iconsToShow = [];

  if (showCutoffUnmetIcon) {
    iconsToShow.push(
      <LegendIconItem
        name={translate('CutoffNotMetLabel')}
        icon={icons.TRACK_FILE}
        kind={kinds.WARNING}
        tooltip={translate('QualityCutoffHasNotBeenMet')}
      />
    );
  }

  return (
    <div className={styles.legend}>
      <div>
        <LegendItem
          status="downloading"
          tooltip={translate('BookCurrentlyDownloading')}
          colorImpairedMode={colorImpairedMode}
        />

        <LegendItem
          status="downloaded"
          tooltip={translate('BookDownloadedAndSorted')}
          colorImpairedMode={colorImpairedMode}
        />
      </div>

      <div>
        <LegendItem
          status="unreleased"
          tooltip={translate('BookNotReleasedYet')}
          colorImpairedMode={colorImpairedMode}
        />

        <LegendItem
          status="partial"
          tooltip={translate('BookPartiallyDownloaded')}
          colorImpairedMode={colorImpairedMode}
        />
      </div>

      <div>
        <LegendItem
          status="unmonitored"
          tooltip={translate('BookIsUnmonitored')}
          colorImpairedMode={colorImpairedMode}
        />

        <LegendItem
          status="missing"
          tooltip={translate('BookFileNotFound')}
          colorImpairedMode={colorImpairedMode}
        />
      </div>

      <div>
        {iconsToShow[0]}
      </div>

      {
        iconsToShow.length > 1 &&
          <div>
            {iconsToShow[1]}
            {iconsToShow[2]}
          </div>
      }
    </div>
  );
}

Legend.propTypes = {
  showCutoffUnmetIcon: PropTypes.bool.isRequired,
  colorImpairedMode: PropTypes.bool.isRequired
};

export default Legend;
