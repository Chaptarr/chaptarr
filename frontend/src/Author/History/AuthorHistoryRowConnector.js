import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchHistory, markAsFailed } from 'Store/Actions/historyActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import AuthorHistoryRow from './AuthorHistoryRow';

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    createBookSelector(),
    (state, props) => props.author,
    (state, props) => props.book,
    (author, book, embeddedAuthor, embeddedBook) => {
      return {
        author: author || embeddedAuthor,
        book: book || embeddedBook
      };
    }
  );
}

const mapDispatchToProps = {
  fetchHistory,
  markAsFailed
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorHistoryRow);
