import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import { createStore } from 'redux';
import AuthorBanner from 'Author/AuthorBanner';
import AuthorImage from 'Author/AuthorImage';
import AuthorPoster from 'Author/AuthorPoster';
import AuthorDetailsHeader from 'Author/Details/AuthorDetailsHeader';
import AuthorIndexPoster from 'Author/Index/Posters/AuthorIndexPoster';
import BookCover from 'Book/BookCover';
import BookDetailsHeader from 'Book/Details/BookDetailsHeader';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

const store = createStore(() => ({
  app: { dimensions: { width: innerWidth, height: innerHeight } },
  settings: { qualityProfiles: { items: [] } }
}));
const root = document.getElementById('root');
window.preparePhotoFixture = fetchTranslations;
window.renderPhotoFixture = (mode, props, remount = true) => {
  if (remount) {
    ReactDOM.unmountComponentAtNode(root);
  }
  const Component = { poster: AuthorPoster, image: AuthorImage, banner: AuthorBanner, book: BookCover, tile: AuthorIndexPoster, details: AuthorDetailsHeader, bookDetails: BookDetailsHeader }[mode];
  const errorProps = mode === 'poster' ? {
    onError: () => {
      window.photoImageErrorCount = (window.photoImageErrorCount || 0) + 1;
    }
  } : {};
  ReactDOM.render(
    <Provider store={store}>
      <MemoryRouter>
        <Component
          {...errorProps}
          lazy={false}
          size={250}
          coverType="poster"
          placeholder="/Content/Images/author-placeholder-muted.png"
          style={{ width: 200, height: 250 }}
          posterWidth={200}
          posterHeight={250}
          showTitle={true}
          selectedMediaType="all"
          sortKey="sortName"
          width={innerWidth - 40}
          ratings={{ value: 0 }}
          statistics={{}}
          monitored={true}
          status="continuing"
          overview=""
          links={[]}
          alternateTitles={[]}
          tags={[]}
          isSaving={false}
          isSmallScreen={innerWidth <= 768}
          onMonitorTogglePress={() => {}}
          {...props}
        />
      </MemoryRouter>
    </Provider>, root);
};
