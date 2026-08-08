import * as app from './appActions';
import * as author from './authorActions';
import * as authorDetails from './authorDetailsActions';
import * as authorFolder from './authorFolderActions';
import * as authorHistory from './authorHistoryActions';
import * as authorIndex from './authorIndexActions';
import * as blocklist from './blocklistActions';
import * as books from './bookActions';
import * as bookFiles from './bookFileActions';
import * as bookHistory from './bookHistoryActions';
import * as bookIndex from './bookIndexActions';
import * as bookInfiniteScroll from './bookInfiniteScrollActions';
import * as bookStudio from './bookshelfActions';
import * as calendar from './calendarActions';
import * as captcha from './captchaActions';
import * as commands from './commandActions';
import * as customFilters from './customFilterActions';
import * as editions from './editionActions';
import * as history from './historyActions';
import * as ignored from './ignoredActions';
import * as interactiveImportActions from './interactiveImportActions';
import * as narrator from './narratorActions';
import * as oAuth from './oAuthActions';
import * as organizePreview from './organizePreviewActions';
import * as paths from './pathActions';
import * as providerOptions from './providerOptionActions';
import * as queue from './queueActions';
import * as quickstart from './quickstartActions';
import * as releases from './releaseActions';
import * as retagPreview from './retagPreviewActions';
import * as search from './searchActions';
import * as series from './seriesActions';
import * as settings from './settingsActions';
import * as system from './systemActions';
import * as tags from './tagActions';
import * as wanted from './wantedActions';

export default [
  app,
  author,
  authorDetails,
  authorFolder,
  authorHistory,
  authorIndex,
  narrator,
  blocklist,
  bookFiles,
  bookHistory,
  bookIndex,
  bookInfiniteScroll,
  books,
  bookStudio,
  calendar,
  captcha,
  commands,
  customFilters,
  editions,
  history,
  ignored,
  interactiveImportActions,
  oAuth,
  organizePreview,
  paths,
  providerOptions,
  queue,
  quickstart,
  releases,
  retagPreview,
  search,
  series,
  settings,
  system,
  tags,
  wanted
];
