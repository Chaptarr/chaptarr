import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import { createStore } from 'redux';
import BookCover from 'Book/BookCover';
import BookDetailsHeader from 'Book/Details/BookDetailsHeader';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

const store = createStore(() => ({
  app: { dimensions: { width: innerWidth, height: innerHeight } },
  settings: { qualityProfiles: { items: [] } }
}));
const root = document.getElementById('root');
const placeholder = '/Content/Images/book-placeholder.png';

window.prepareBookCoverFixture = fetchTranslations;
window.renderBookCoverFixture = (mode, image, count = 1) => {
  ReactDOM.unmountComponentAtNode(root);
  let content = null;

  if (mode === 'list') {
    content = Array.from({ length: count }, (_, index) => (
      <BookCover
        key={index}
        images={[image]}
        size={250}
        lazy={false}
        placeholder={placeholder}
      />
    ));
  } else if (mode === 'details') {
    content = (
      <BookDetailsHeader
        id={1}
        width={900}
        titleSlug="book-title"
        title="Book title"
        seriesTitle=""
        overview=""
        statistics={{ sizeOnDisk: 0, bookFileCount: 0 }}
        releaseDate={null}
        ratings={{ value: 0 }}
        images={[image]}
        links={[]}
        audiobookMonitored={false}
        ebookMonitored={false}
        mediaType="audiobook"
        shortDateFormat="yyyy-MM-dd"
        isSaving={false}
        isSmallScreen={false}
        author={{ id: 1, titleSlug: 'author-name', authorName: 'Author', images: [] }}
        onMonitorTogglePress={() => {}}
      />
    );
  } else {
    content = (
      <BookCover
        images={[image]}
        size={250}
        lazy={false}
        placeholder={placeholder}
      />
    );
  }

  ReactDOM.render(
    <Provider store={store}>
      <MemoryRouter>
        <div>{content}</div>
      </MemoryRouter>
    </Provider>, root);
};
