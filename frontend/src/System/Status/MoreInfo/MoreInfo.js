import React, { Component } from 'react';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItemDescription from 'Components/DescriptionList/DescriptionListItemDescription';
import DescriptionListItemTitle from 'Components/DescriptionList/DescriptionListItemTitle';
import FieldSet from 'Components/FieldSet';
import Link from 'Components/Link/Link';
import translate from 'Utilities/String/translate';

class MoreInfo extends Component {

  //
  // Render

  render() {
    return (
      <FieldSet legend={translate('MoreInfo')}>
        <DescriptionList>
          {/* No Home Page entry: chaptarr.com does not serve a site yet; re-add if a landing page ships. */}
          <DescriptionListItemTitle>{translate('Wiki')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/snapetech/chaptarrng#documentation">{translate('Wiki')}</Link>
          </DescriptionListItemDescription>

          {/* No Reddit entry: r/chaptarr is already taken and not affiliated with Chaptarr, and no official subreddit is planned. */}

          <DescriptionListItemTitle>{translate('Discord')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://discord.gg/nqFGsGUug2">{translate('ChaptarrOnDiscord')}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{translate('Source')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/snapetech/chaptarrng">{'github.com/snapetech/chaptarrng'}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{translate('FeatureRequests')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/snapetech/chaptarrng/issues">{'github.com/snapetech/chaptarrng/issues'}</Link>
          </DescriptionListItemDescription>

        </DescriptionList>
      </FieldSet>
    );
  }
}

MoreInfo.propTypes = {

};

export default MoreInfo;
