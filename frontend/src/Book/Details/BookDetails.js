import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { Tab, TabList, TabPanel, Tabs } from 'react-tabs';
import AuthorHistoryTable from 'Author/History/AuthorHistoryTable';
import DeleteBookModal from 'Book/Delete/DeleteBookModal';
import EditBookModalConnector from 'Book/Edit/EditBookModalConnector';
import BookFileEditorTable from 'BookFile/Editor/BookFileEditorTable';
import IconButton from 'Components/Link/IconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import SwipeHeaderConnector from 'Components/Swipe/SwipeHeaderConnector';
import { icons } from 'Helpers/Props';
import InteractiveSearchFilterMenuConnector from 'InteractiveSearch/InteractiveSearchFilterMenuConnector';
import InteractiveSearchTable from 'InteractiveSearch/InteractiveSearchTable';
import OrganizePreviewModalConnector from 'Organize/OrganizePreviewModalConnector';
import RetagPreviewModalConnector from 'Retag/RetagPreviewModalConnector';
import translate from 'Utilities/String/translate';
import BookDetailsHeaderConnector from './BookDetailsHeaderConnector';
import BookChaptersTable from './Chapters/BookChaptersTable';
import BookMatchProvenanceTable from './Matching/BookMatchProvenanceTable';
import styles from './BookDetails.css';

class BookDetails extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isOrganizeModalOpen: false,
      isRetagModalOpen: false,
      isEditBookModalOpen: false,
      isDeleteBookModalOpen: false,
      selectedTabIndex: 0
    };
  }

  componentDidUpdate(prevProps) {
    const { chapters } = this.props;
    const { selectedTabIndex } = this.state;

    if (
      prevProps.chapters.length &&
      !chapters.length &&
      selectedTabIndex > 3
    ) {
      this.setState({ selectedTabIndex: 0 });
    }
  }

  //
  // Listeners

  onOrganizePress = () => {
    this.setState({ isOrganizeModalOpen: true });
  };

  onOrganizeModalClose = () => {
    this.setState({ isOrganizeModalOpen: false });
  };

  onRetagPress = () => {
    this.setState({ isRetagModalOpen: true });
  };

  onRetagModalClose = () => {
    this.setState({ isRetagModalOpen: false });
  };

  onEditBookPress = () => {
    this.setState({ isEditBookModalOpen: true });
  };

  onEditBookModalClose = () => {
    this.setState({ isEditBookModalOpen: false });
  };

  onDeleteBookPress = () => {
    this.setState({
      isEditBookModalOpen: false,
      isDeleteBookModalOpen: true
    });
  };

  onDeleteBookModalClose = () => {
    this.setState({ isDeleteBookModalOpen: false });
  };

  onTabSelect = (index, lastIndex) => {
    this.setState({ selectedTabIndex: index });
  };

  //
  // Render

  render() {
    const {
      id,
      title,
      isRefreshing,
      isFetching,
      isPopulated,
      bookFilesError,
      hasBookFiles,
      bookFiles,
      chapters,
      mediaType,
      author,
      previousBook,
      nextBook,
      hasBookNavigation,
      isSearching,
      onRefreshPress,
      onSearchPress,
      statistics = {}
    } = this.props;

    const {
      bookFileCount = 0
    } = statistics;

    const {
      isOrganizeModalOpen,
      isRetagModalOpen,
      isEditBookModalOpen,
      isDeleteBookModalOpen,
      selectedTabIndex
    } = this.state;

    return (
      <PageContent title={title}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label={translate('Refresh')}
              iconName={icons.REFRESH}
              spinningName={icons.REFRESH}
              title={translate('RefreshInformation')}
              isSpinning={isRefreshing}
              onPress={onRefreshPress}
            />

            <PageToolbarButton
              label={translate('SearchBook')}
              iconName={icons.SEARCH}
              isSpinning={isSearching}
              onPress={onSearchPress}
            />

            <PageToolbarSeparator />

            <PageToolbarButton
              label={translate('PreviewRename')}
              iconName={icons.ORGANIZE}
              isDisabled={!hasBookFiles}
              onPress={this.onOrganizePress}
            />

            <PageToolbarButton
              label={translate('PreviewRetag')}
              iconName={icons.RETAG}
              isDisabled={!hasBookFiles}
              onPress={this.onRetagPress}
            />

            <PageToolbarSeparator />

            <PageToolbarButton
              label={translate('Edit')}
              iconName={icons.EDIT}
              onPress={this.onEditBookPress}
            />

            <PageToolbarButton
              label={translate('Delete')}
              iconName={icons.DELETE}
              onPress={this.onDeleteBookPress}
            />

          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody innerClassName={styles.innerContentBody}>
          <SwipeHeaderConnector
            className={styles.header}
            nextLink={`/book/${nextBook.id}`}
            nextComponent={(width) => (
              <BookDetailsHeaderConnector
                bookId={nextBook.id}
                author={author}
                width={width}
              />
            )}
            prevLink={`/book/${previousBook.id}`}
            prevComponent={(width) => (
              <BookDetailsHeaderConnector
                bookId={previousBook.id}
                author={author}
                width={width}
              />
            )}
            currentComponent={(width) => (
              <BookDetailsHeaderConnector
                bookId={id}
                author={author}
                width={width}
              />
            )}
          >
            <div className={styles.bookNavigationButtons}>
              <IconButton
                className={styles.bookNavigationButton}
                name={icons.ARROW_LEFT}
                size={30}
                title={translate('GoToInterp', [previousBook.title])}
                isDisabled={!hasBookNavigation}
                to={`/book/${previousBook.id}`}
              />

              <IconButton
                className={styles.bookUpButton}
                name={icons.ARROW_UP}
                size={30}
                title={translate('GoToInterp', [author.authorName])}
                to={`/author/${author.id}`}
              />

              <IconButton
                className={styles.bookNavigationButton}
                name={icons.ARROW_RIGHT}
                size={30}
                title={translate('GoToInterp', [nextBook.title])}
                isDisabled={!hasBookNavigation}
                to={`/book/${nextBook.id}`}
              />
            </div>
          </SwipeHeaderConnector>

          <div className={styles.contentContainer}>
            {
              !isPopulated && !bookFilesError &&
                <LoadingIndicator />
            }

            {
              !isFetching && bookFilesError &&
                <div>
                  {translate('LoadingBookFilesFailed')}
                </div>
            }

            <Tabs selectedIndex={selectedTabIndex} onSelect={this.onTabSelect}>
              <TabList
                className={styles.tabList}
              >
                <Tab
                  className={styles.tab}
                  selectedClassName={styles.selectedTab}
                >
                  {translate('History')}
                </Tab>

                <Tab
                  className={styles.tab}
                  selectedClassName={styles.selectedTab}
                >
                  {translate('Search')}
                </Tab>

                <Tab
                  className={styles.tab}
                  selectedClassName={styles.selectedTab}
                >
                  {translate('Matching')}
                </Tab>

                <Tab
                  className={styles.tab}
                  selectedClassName={styles.selectedTab}
                >
                  {translate('FilesTotal', [bookFileCount])}
                </Tab>

                {
                  chapters.length ?
                    <Tab
                      className={styles.tab}
                      selectedClassName={styles.selectedTab}
                    >
                      {translate('Chapters')}
                    </Tab> :
                    null
                }

                {
                  selectedTabIndex === 1 &&
                    <div className={styles.filterIcon}>
                      <InteractiveSearchFilterMenuConnector
                        type="book"
                      />
                    </div>
                }

              </TabList>

              <TabPanel>
                <AuthorHistoryTable
                  authorId={author.id}
                  bookId={id}
                />
              </TabPanel>

              <TabPanel>
                <InteractiveSearchTable
                  bookId={id}
                  type="book"
                  selectedMediaType={mediaType === 'ebook' ? 'ebook' : 'audiobook'}
                />
              </TabPanel>

              <TabPanel>
                <BookMatchProvenanceTable items={bookFiles} />
              </TabPanel>

              <TabPanel>
                <BookFileEditorTable
                  authorId={author.id}
                  bookId={id}
                />
              </TabPanel>

              {
                chapters.length ?
                  <TabPanel>
                    <BookChaptersTable chapters={chapters} />
                  </TabPanel> :
                  null
              }
            </Tabs>
          </div>

          <OrganizePreviewModalConnector
            isOpen={isOrganizeModalOpen}
            authorId={author.id}
            bookId={id}
            mediaType={mediaType}
            onModalClose={this.onOrganizeModalClose}
          />

          <RetagPreviewModalConnector
            isOpen={isRetagModalOpen}
            authorId={author.id}
            bookId={id}
            onModalClose={this.onRetagModalClose}
          />

          <EditBookModalConnector
            isOpen={isEditBookModalOpen}
            bookId={id}
            authorId={author.id}
            onModalClose={this.onEditBookModalClose}
            onDeleteAuthorPress={this.onDeleteBookPress}
          />

          <DeleteBookModal
            isOpen={isDeleteBookModalOpen}
            bookId={id}
            authorId={author.id}
            onModalClose={this.onDeleteBookModalClose}
          />

        </PageContentBody>
      </PageContent>
    );
  }
}

BookDetails.propTypes = {
  id: PropTypes.number.isRequired,
  titleSlug: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  seriesTitle: PropTypes.string,
  pageCount: PropTypes.number,
  overview: PropTypes.string,
  releaseDate: PropTypes.string.isRequired,
  ratings: PropTypes.object.isRequired,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  links: PropTypes.arrayOf(PropTypes.object).isRequired,
  statistics: PropTypes.object.isRequired,
  monitored: PropTypes.bool.isRequired,
  mediaType: PropTypes.string,
  shortDateFormat: PropTypes.string.isRequired,
  isSaving: PropTypes.bool.isRequired,
  isRefreshing: PropTypes.bool,
  isSearching: PropTypes.bool,
  isFetching: PropTypes.bool,
  isPopulated: PropTypes.bool,
  bookFilesError: PropTypes.object,
  hasBookFiles: PropTypes.bool.isRequired,
  bookFiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  chapters: PropTypes.arrayOf(PropTypes.object).isRequired,
  author: PropTypes.object,
  previousBook: PropTypes.object,
  nextBook: PropTypes.object,
  hasBookNavigation: PropTypes.bool,
  isSmallScreen: PropTypes.bool.isRequired,
  onMonitorTogglePress: PropTypes.func.isRequired,
  onRefreshPress: PropTypes.func,
  onSearchPress: PropTypes.func.isRequired
};

BookDetails.defaultProps = {
  isSaving: false,
  bookFiles: [],
  chapters: []
};

export default BookDetails;
