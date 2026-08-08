import PropTypes from 'prop-types';
import React from 'react';
import { Redirect, Route } from 'react-router-dom';
import BlocklistConnector from 'Activity/Blocklist/BlocklistConnector';
import HistoryConnector from 'Activity/History/HistoryConnector';
import IgnoredDownloadsConnector from 'Activity/Ignored/IgnoredDownloadsConnector';
import QueueConnector from 'Activity/Queue/QueueConnector';
import AuthorDetailsPageConnector from 'Author/Details/AuthorDetailsPageConnector';
import AuthorIndexConnector from 'Author/Index/AuthorIndexConnector';
import BookDetailsPageConnector from 'Book/Details/BookDetailsPageConnector';
import BookIndexConnector from 'Book/Index/BookIndexConnector';
import BookshelfConnector from 'Bookshelf/BookshelfConnector';
import CalendarPageConnector from 'Calendar/CalendarPageConnector';
import NavigationErrorBoundary from 'Components/Error/NavigationErrorBoundary';
import NotFound from 'Components/NotFound';
import Switch from 'Components/Router/Switch';
import SeriesDetailsPageConnector from 'Series/Details/SeriesDetailsPageConnector';
import AddNewItemConnector from 'Search/AddNewItemConnector';
import ConversionSettingsConnector from 'Settings/Conversion/ConversionSettingsConnector';
import CustomFormatSettingsConnector from 'Settings/CustomFormats/CustomFormatSettingsConnector';
import DevelopmentSettingsConnector from 'Settings/Development/DevelopmentSettingsConnector';
import DownloadClientSettingsConnector from 'Settings/DownloadClients/DownloadClientSettingsConnector';
import GeneralSettingsConnector from 'Settings/General/GeneralSettingsConnector';
import ImportListSettingsConnector from 'Settings/ImportLists/ImportListSettingsConnector';
import IndexerSettingsConnector from 'Settings/Indexers/IndexerSettingsConnector';
import MediaManagementConnector from 'Settings/MediaManagement/MediaManagementConnector';
import MetadataSettings from 'Settings/Metadata/MetadataSettings';
import NotificationSettings from 'Settings/Notifications/NotificationSettings';
import Profiles from 'Settings/Profiles/Profiles';
import QualityConnector from 'Settings/Quality/QualityConnector';
import Settings from 'Settings/Settings';
import TagSettings from 'Settings/Tags/TagSettings';
import UISettingsConnector from 'Settings/UI/UISettingsConnector';
import BackupsConnector from 'System/Backup/BackupsConnector';
import LogsTableConnector from 'System/Events/LogsTableConnector';
import Logs from 'System/Logs/Logs';
import QuickstartConnector from 'System/Quickstart/QuickstartConnector';
import Status from 'System/Status/Status';
import Tasks from 'System/Tasks/Tasks';
import Updates from 'System/Updates/Updates';
import UnmappedFilesTableConnector from 'UnmappedFiles/UnmappedFilesTableConnector';
import getPathWithUrlBase from 'Utilities/getPathWithUrlBase';
import CutoffUnmetConnector from 'Wanted/CutoffUnmet/CutoffUnmetConnector';
import MissingConnector from 'Wanted/Missing/MissingConnector';

function AppRoutes(props) {
  const {
    app
  } = props;

  return (
    <NavigationErrorBoundary>
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
    </NavigationErrorBoundary>
  );
}

AppRoutes.propTypes = {
  app: PropTypes.func.isRequired
};

export default AppRoutes;
