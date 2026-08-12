import PropTypes from 'prop-types';
import React, { lazy, Suspense } from 'react';
import { Redirect, Route } from 'react-router-dom';
import NavigationErrorBoundary from 'Components/Error/NavigationErrorBoundary';
import NotFound from 'Components/NotFound';
import Switch from 'Components/Router/Switch';
import getPathWithUrlBase from 'Utilities/getPathWithUrlBase';

const BlocklistConnector = lazy(() => import('Activity/Blocklist/BlocklistConnector'));
const HistoryConnector = lazy(() => import('Activity/History/HistoryConnector'));
const IgnoredDownloadsConnector = lazy(() => import('Activity/Ignored/IgnoredDownloadsConnector'));
const QueueConnector = lazy(() => import('Activity/Queue/QueueConnector'));
const AuthorDetailsPageConnector = lazy(() => import('Author/Details/AuthorDetailsPageConnector'));
const AuthorIndexConnector = lazy(() => import('Author/Index/AuthorIndexConnector'));
const BookDetailsPageConnector = lazy(() => import('Book/Details/BookDetailsPageConnector'));
const BookIndexConnector = lazy(() => import('Book/Index/BookIndexConnector'));
const BookshelfConnector = lazy(() => import('Bookshelf/BookshelfConnector'));
const CalendarPageConnector = lazy(() => import('Calendar/CalendarPageConnector'));
const SeriesDetailsPageConnector = lazy(() => import('Series/Details/SeriesDetailsPageConnector'));
const AddNewItemConnector = lazy(() => import('Search/AddNewItemConnector'));
const ConversionSettingsConnector = lazy(() => import('Settings/Conversion/ConversionSettingsConnector'));
const CustomFormatSettingsConnector = lazy(() => import('Settings/CustomFormats/CustomFormatSettingsConnector'));
const DevelopmentSettingsConnector = lazy(() => import('Settings/Development/DevelopmentSettingsConnector'));
const DownloadClientSettingsConnector = lazy(() => import('Settings/DownloadClients/DownloadClientSettingsConnector'));
const GeneralSettingsConnector = lazy(() => import('Settings/General/GeneralSettingsConnector'));
const ImportListSettingsConnector = lazy(() => import('Settings/ImportLists/ImportListSettingsConnector'));
const IndexerSettingsConnector = lazy(() => import('Settings/Indexers/IndexerSettingsConnector'));
const MediaManagementConnector = lazy(() => import('Settings/MediaManagement/MediaManagementConnector'));
const MetadataSettings = lazy(() => import('Settings/Metadata/MetadataSettings'));
const NotificationSettings = lazy(() => import('Settings/Notifications/NotificationSettings'));
const Profiles = lazy(() => import('Settings/Profiles/Profiles'));
const QualityConnector = lazy(() => import('Settings/Quality/QualityConnector'));
const Settings = lazy(() => import('Settings/Settings'));
const TagSettings = lazy(() => import('Settings/Tags/TagSettings'));
const UISettingsConnector = lazy(() => import('Settings/UI/UISettingsConnector'));
const BackupsConnector = lazy(() => import('System/Backup/BackupsConnector'));
const LogsTableConnector = lazy(() => import('System/Events/LogsTableConnector'));
const Logs = lazy(() => import('System/Logs/Logs'));
const QuickstartConnector = lazy(() => import('System/Quickstart/QuickstartConnector'));
const Status = lazy(() => import('System/Status/Status'));
const Tasks = lazy(() => import('System/Tasks/Tasks'));
const Updates = lazy(() => import('System/Updates/Updates'));
const UnmappedFilesTableConnector = lazy(() => import('UnmappedFiles/UnmappedFilesTableConnector'));
const CutoffUnmetConnector = lazy(() => import('Wanted/CutoffUnmet/CutoffUnmetConnector'));
const MissingConnector = lazy(() => import('Wanted/Missing/MissingConnector'));

import LoadingIndicator from 'Components/Loading/LoadingIndicator';

function AppRoutes(props) {
  const {
    app
  } = props;

  return (
    <NavigationErrorBoundary>
      <Suspense fallback={<LoadingIndicator />}>
        <Switch>
          {/*
            Author
          */}

          <Route
            exact={true}
            path="/"
            component={AuthorIndexConnector}
          />

          {
            window.Chaptarr.urlBase &&
              <Route
                exact={true}
                path="/"
                addUrlBase={false}
                render={() => {
                  return (
                    <Redirect
                      to={getPathWithUrlBase('/')}
                      component={app}
                    />
                  );
                }}
              />
          }

          <Route
            path="/authors"
            component={AuthorIndexConnector}
          />

          <Route
            exact={true}
            path="/authoreditor"
            render={() => (
              <Redirect to={getPathWithUrlBase('/authors')} />
            )}
          />

          <Route
            path="/add/search"
            component={AddNewItemConnector}
          />

          <Route
            exact={true}
            path="/shelf"
            component={BookshelfConnector}
          />

          <Route
            exact={true}
            path="/books"
            component={BookIndexConnector}
          />

          <Route
            path="/unmapped"
            component={UnmappedFilesTableConnector}
          />

          <Route
            path="/author/:id"
            component={AuthorDetailsPageConnector}
          />

          <Route
            path="/:mediaScope(ebook|ebooks|audiobook|audiobooks)/book/:bookId(.+)"
            component={BookDetailsPageConnector}
          />

          <Route
            path="/book/:bookId(.+)"
            component={BookDetailsPageConnector}
          />

          <Route
            path="/series/:localSeriesId"
            component={SeriesDetailsPageConnector}
          />

          {/*
            Calendar
          */}

          <Route
            path="/calendar"
            component={CalendarPageConnector}
          />

          {/*
            Activity
          */}

          <Route
            path="/activity/history"
            component={HistoryConnector}
          />

          <Route
            path="/activity/queue"
            component={QueueConnector}
          />

          <Route
            path="/activity/blocklist"
            component={BlocklistConnector}
          />

          <Route
            path="/activity/ignored"
            component={IgnoredDownloadsConnector}
          />

          {/*
            Wanted
          */}

          <Route
            path="/wanted/missing"
            component={MissingConnector}
          />

          <Route
            path="/wanted/cutoffunmet"
            component={CutoffUnmetConnector}
          />

          {/*
            Settings
          */}

          <Route
            exact={true}
            path="/settings"
            component={Settings}
          />

          <Route
            path="/settings/mediamanagement"
            component={MediaManagementConnector}
          />

          <Route
            path="/settings/conversion"
            component={ConversionSettingsConnector}
          />

          <Route
            path="/settings/profiles"
            component={Profiles}
          />

          <Route
            path="/settings/customformats"
            component={CustomFormatSettingsConnector}
          />

          <Route
            path="/settings/indexers"
            component={IndexerSettingsConnector}
          />

          <Route
            path="/settings/downloadclients"
            component={DownloadClientSettingsConnector}
          />

          <Route
            path="/settings/importlists"
            component={ImportListSettingsConnector}
          />

          <Route
            path="/settings/connect"
            component={NotificationSettings}
          />

          <Route
            path="/settings/metadata"
            component={MetadataSettings}
          />

          <Route
            path="/settings/tags"
            component={TagSettings}
          />

          <Route
            path="/settings/general"
            component={GeneralSettingsConnector}
          />

          <Route
            path="/settings/ui"
            component={UISettingsConnector}
          />

          <Route
            path="/settings/development"
            component={DevelopmentSettingsConnector}
          />

          {/*
            System
          */}

          <Route
            path="/system/status"
            component={Status}
          />

          <Route
            path="/system/quickstart"
            component={QuickstartConnector}
          />

          <Route
            path="/system/tasks"
            component={Tasks}
          />

          <Route
            path="/system/backup"
            component={BackupsConnector}
          />

          <Route
            path="/system/updates"
            component={Updates}
          />

          <Route
            path="/system/events"
            component={LogsTableConnector}
          />

          <Route
            path="/system/logs/files"
            component={Logs}
          />

          {/*
            Not Found
          */}

          <Route
            path="*"
            component={NotFound}
          />

        </Switch>
      </Suspense>
    </NavigationErrorBoundary>
  );
}

AppRoutes.propTypes = {
  app: PropTypes.func.isRequired
};

export default AppRoutes;
