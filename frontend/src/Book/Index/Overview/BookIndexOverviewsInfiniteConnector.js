import { connect } from 'react-redux';
import withInfiniteBooks from '../withInfiniteBooks';
import BookIndexOverviews from './BookIndexOverviews';
import { createMapStateToProps } from './BookIndexOverviewsConnector';

export default connect(createMapStateToProps)(withInfiniteBooks(BookIndexOverviews));
