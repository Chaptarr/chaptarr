import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createDeepEqualSelector from 'Store/Selectors/createDeepEqualSelector';
import { getAuthorMediaTypeMonitoringStatus, isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import AuthorIndexFooter from './AuthorIndexFooter';

function createUnoptimizedSelector() {
  return createSelector(
    createClientSideCollectionSelector('authors', 'authorIndex'),
    (state, ownProps) => ownProps && ownProps.selectedMediaType,
    (authors, selectedMediaTypeProp) => {
      const selectedMediaType = selectedMediaTypeProp || 'all';

      return {
        items: authors.items
          .filter((author) => {
            const audiobookStatus = getAuthorMediaTypeRootFolderStatus(author, 'audiobook');
            const ebookStatus = getAuthorMediaTypeRootFolderStatus(author, 'ebook');

            switch (selectedMediaType) {
              case 'audiobook':
                return audiobookStatus.hasRootFolder;
              case 'ebook':
                return ebookStatus.hasRootFolder;
              case 'all':
              default:
                return audiobookStatus.hasRootFolder || ebookStatus.hasRootFolder;
            }
          })
          .map((author) => {
            let statistics = author.statistics;
            if (selectedMediaType === 'audiobook' && author.audiobookStatistics) {
              statistics = author.audiobookStatistics;
            } else if (selectedMediaType === 'ebook' && author.ebookStatistics) {
              statistics = author.ebookStatistics;
            }

            return {
              id: author.id,
              monitored: isAuthorMonitoredForSelection(author, selectedMediaType),
              audiobookMonitoring: getAuthorMediaTypeMonitoringStatus(author, 'audiobook'),
              ebookMonitoring: getAuthorMediaTypeMonitoringStatus(author, 'ebook'),
              status: author.status,
              statistics
            };
          }),
        mediaType: selectedMediaType
      };
    }
  );
}

function createAuthorSelector() {
  return createDeepEqualSelector(
    createUnoptimizedSelector(),
    (data) => data
  );
}

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    (data) => {
      return {
        author: data.items,
        mediaType: data.mediaType || 'all'
      };
    }
  );
}

export default connect(createMapStateToProps)(AuthorIndexFooter);
