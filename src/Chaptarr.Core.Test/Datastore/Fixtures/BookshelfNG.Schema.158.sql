-- Schema generated from a fresh BookshelfNG 0.4.21.69 database.
-- Source: https://github.com/snapetech/bookshelfng/releases/tag/main-v0.4.21.69
-- Official Linux x64 release archive checksum verified against the published .sha256 asset.
-- The source database was migrated by the release binary to schema version 158.

CREATE TABLE "AuthorMetadata" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ForeignAuthorId" TEXT NOT NULL, "TitleSlug" TEXT NOT NULL, "Name" TEXT NOT NULL, "Overview" TEXT, "Disambiguation" TEXT, "Gender" TEXT, "Hometown" TEXT, "Born" DATETIME, "Died" DATETIME, "Status" INTEGER NOT NULL, "Images" TEXT NOT NULL, "Links" TEXT, "Genres" TEXT, "Ratings" TEXT, "Aliases" TEXT NOT NULL, "SortName" TEXT NOT NULL, "NameLastFirst" TEXT NOT NULL, "SortNameLastFirst" TEXT NOT NULL);

CREATE TABLE "Authors" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "CleanName" TEXT NOT NULL, "Path" TEXT NOT NULL, "Monitored" INTEGER NOT NULL, "LastInfoSync" DATETIME, "QualityProfileId" INTEGER, "Tags" TEXT, "Added" DATETIME, "AddOptions" TEXT, "MetadataProfileId" INTEGER NOT NULL, "AuthorMetadataId" INTEGER NOT NULL, "MonitorNewItems" INTEGER NOT NULL, "EbookPath" TEXT, "AudiobookPath" TEXT, "EbookQualityProfileId" INTEGER, "AudiobookQualityProfileId" INTEGER, "EbookMetadataProfileId" INTEGER, "AudiobookMetadataProfileId" INTEGER);

CREATE TABLE "Blocklist" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "SourceTitle" TEXT NOT NULL, "Quality" TEXT NOT NULL, "Date" DATETIME NOT NULL, "PublishedDate" DATETIME, "Size" INTEGER, "Protocol" INTEGER, "Indexer" TEXT, "Message" TEXT, "TorrentInfoHash" TEXT, "AuthorId" INTEGER NOT NULL, "BookIds" TEXT NOT NULL, "IndexerFlags" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "BookFiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "EditionId" INTEGER NOT NULL, "CalibreId" INTEGER NOT NULL, "Quality" TEXT NOT NULL, "Size" INTEGER NOT NULL, "SceneName" TEXT, "DateAdded" DATETIME, "ReleaseGroup" TEXT, "MediaInfo" TEXT, "Modified" DATETIME, "Path" TEXT NOT NULL, "Part" INTEGER NOT NULL, "OriginalFilePath" TEXT, "IndexerFlags" INTEGER NOT NULL DEFAULT 0, "LastRemoteSearchTime" DATETIME);

CREATE TABLE "Books" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AuthorMetadataId" INTEGER NOT NULL, "ForeignBookId" TEXT NOT NULL, "TitleSlug" TEXT NOT NULL, "Title" TEXT NOT NULL, "ReleaseDate" DATETIME, "Links" TEXT, "Genres" TEXT, "Ratings" TEXT, "CleanTitle" TEXT NOT NULL, "Monitored" INTEGER NOT NULL, "AnyEditionOk" INTEGER NOT NULL, "LastInfoSync" DATETIME, "Added" DATETIME, "AddOptions" TEXT, "RelatedBooks" TEXT NOT NULL, "LastSearchTime" DATETIME);

CREATE TABLE "Commands" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "Body" TEXT NOT NULL, "Priority" INTEGER NOT NULL, "Status" INTEGER NOT NULL, "QueuedAt" DATETIME NOT NULL, "StartedAt" DATETIME, "EndedAt" DATETIME, "Duration" TEXT, "Exception" TEXT, "Trigger" INTEGER NOT NULL, "Result" INTEGER NOT NULL DEFAULT 1);

CREATE TABLE "Config" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Key" TEXT NOT NULL, "Value" TEXT NOT NULL);

CREATE TABLE "CustomFilters" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Type" TEXT NOT NULL, "Label" TEXT NOT NULL, "Filters" TEXT NOT NULL);

CREATE TABLE "CustomFormats" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "Specifications" TEXT NOT NULL DEFAULT '[]', "IncludeCustomFormatWhenRenaming" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "DelayProfiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "EnableUsenet" INTEGER NOT NULL, "EnableTorrent" INTEGER NOT NULL, "PreferredProtocol" INTEGER NOT NULL, "UsenetDelay" INTEGER NOT NULL, "TorrentDelay" INTEGER NOT NULL, "Order" INTEGER NOT NULL, "Tags" TEXT NOT NULL, "BypassIfHighestQuality" INTEGER NOT NULL DEFAULT 0, "BypassIfAboveCustomFormatScore" INTEGER NOT NULL DEFAULT 0, "MinimumCustomFormatScore" INTEGER);

CREATE TABLE "DownloadClientStatus" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ProviderId" INTEGER NOT NULL, "InitialFailure" DATETIME, "MostRecentFailure" DATETIME, "EscalationLevel" INTEGER NOT NULL, "DisabledTill" DATETIME);

CREATE TABLE "DownloadClients" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Enable" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Implementation" TEXT NOT NULL, "Settings" TEXT NOT NULL, "ConfigContract" TEXT NOT NULL, "Priority" INTEGER NOT NULL DEFAULT 1, "Tags" TEXT, "RemoveCompletedDownloads" INTEGER NOT NULL DEFAULT 1, "RemoveFailedDownloads" INTEGER NOT NULL DEFAULT 1);

CREATE TABLE "DownloadHistory" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "EventType" INTEGER NOT NULL, "AuthorId" INTEGER NOT NULL, "DownloadId" TEXT NOT NULL, "SourceTitle" TEXT NOT NULL, "Date" DATETIME, "Protocol" INTEGER, "IndexerId" INTEGER, "DownloadClientId" INTEGER, "Release" TEXT, "Data" TEXT);

CREATE TABLE "Editions" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "BookId" INTEGER NOT NULL, "ForeignEditionId" TEXT NOT NULL, "Isbn13" TEXT, "Asin" TEXT, "Title" TEXT NOT NULL, "TitleSlug" TEXT NOT NULL, "Language" TEXT, "Overview" TEXT, "Format" TEXT, "IsEbook" INTEGER, "Disambiguation" TEXT, "Publisher" TEXT, "PageCount" INTEGER, "ReleaseDate" DATETIME, "Images" TEXT NOT NULL, "Links" TEXT, "Ratings" TEXT, "Monitored" INTEGER NOT NULL, "ManualAdd" INTEGER NOT NULL, "Narrator" TEXT);

CREATE TABLE "ExtraFiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AuthorId" INTEGER NOT NULL, "BookId" INTEGER NOT NULL, "BookFileId" INTEGER NOT NULL, "RelativePath" TEXT NOT NULL, "Extension" TEXT NOT NULL, "Added" DATETIME NOT NULL, "LastUpdated" DATETIME NOT NULL);

CREATE TABLE "History" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "SourceTitle" TEXT NOT NULL, "Date" DATETIME NOT NULL, "Quality" TEXT NOT NULL, "Data" TEXT NOT NULL, "EventType" INTEGER, "DownloadId" TEXT, "AuthorId" INTEGER NOT NULL, "BookId" INTEGER NOT NULL);

CREATE TABLE "ImportListExclusions" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ForeignId" TEXT NOT NULL, "Name" TEXT NOT NULL);

CREATE TABLE "ImportListStatus" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ProviderId" INTEGER NOT NULL, "InitialFailure" DATETIME, "MostRecentFailure" DATETIME, "EscalationLevel" INTEGER NOT NULL, "DisabledTill" DATETIME, "LastInfoSync" DATETIME);

CREATE TABLE "ImportLists" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "Implementation" TEXT NOT NULL, "Settings" TEXT, "ConfigContract" TEXT, "EnableAutomaticAdd" INTEGER, "RootFolderPath" TEXT NOT NULL, "ShouldMonitor" INTEGER NOT NULL, "ProfileId" INTEGER NOT NULL, "MetadataProfileId" INTEGER NOT NULL, "Tags" TEXT, "ShouldSearch" INTEGER NOT NULL DEFAULT 1, "ShouldMonitorExisting" INTEGER NOT NULL DEFAULT 0, "MonitorNewItems" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "IndexerStatus" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ProviderId" INTEGER NOT NULL, "InitialFailure" DATETIME, "MostRecentFailure" DATETIME, "EscalationLevel" INTEGER NOT NULL, "DisabledTill" DATETIME, "LastRssSyncReleaseInfo" TEXT);

CREATE TABLE "Indexers" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "Implementation" TEXT NOT NULL, "Settings" TEXT, "ConfigContract" TEXT, "EnableRss" INTEGER, "EnableAutomaticSearch" INTEGER, "EnableInteractiveSearch" INTEGER NOT NULL, "Priority" INTEGER NOT NULL DEFAULT 25, "Tags" TEXT, "DownloadClientId" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "Metadata" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Enable" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Implementation" TEXT NOT NULL, "Settings" TEXT NOT NULL, "ConfigContract" TEXT NOT NULL);

CREATE TABLE "MetadataFiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AuthorId" INTEGER NOT NULL, "Consumer" TEXT NOT NULL, "Type" INTEGER NOT NULL, "RelativePath" TEXT NOT NULL, "LastUpdated" DATETIME NOT NULL, "BookId" INTEGER, "BookFileId" INTEGER, "Hash" TEXT, "Added" DATETIME, "Extension" TEXT NOT NULL);

CREATE TABLE "MetadataProfiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "MinPopularity" NUMERIC NOT NULL, "SkipMissingDate" INTEGER NOT NULL, "SkipMissingIsbn" INTEGER NOT NULL, "SkipPartsAndSets" INTEGER NOT NULL, "SkipSeriesSecondary" INTEGER NOT NULL, "AllowedLanguages" TEXT, "MinPages" INTEGER NOT NULL DEFAULT 0, "Ignored" TEXT, "PreferredEditionTerms" TEXT, "PreferredSeries" TEXT);

CREATE TABLE "NamingConfig" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ReplaceIllegalCharacters" INTEGER NOT NULL DEFAULT 1, "AuthorFolderFormat" TEXT, "RenameBooks" INTEGER, "StandardBookFormat" TEXT, "ColonReplacementFormat" INTEGER NOT NULL DEFAULT 4);

CREATE TABLE "NotificationStatus" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ProviderId" INTEGER NOT NULL, "InitialFailure" DATETIME, "MostRecentFailure" DATETIME, "EscalationLevel" INTEGER NOT NULL, "DisabledTill" DATETIME);

CREATE TABLE "Notifications" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "OnGrab" INTEGER NOT NULL, "Settings" TEXT NOT NULL, "Implementation" TEXT NOT NULL, "ConfigContract" TEXT, "OnUpgrade" INTEGER, "Tags" TEXT, "OnRename" INTEGER NOT NULL, "OnReleaseImport" INTEGER NOT NULL, "OnHealthIssue" INTEGER NOT NULL, "IncludeHealthWarnings" INTEGER NOT NULL, "OnDownloadFailure" INTEGER NOT NULL, "OnImportFailure" INTEGER NOT NULL, "OnBookRetag" INTEGER NOT NULL, "OnAuthorDelete" INTEGER NOT NULL DEFAULT 0, "OnBookDelete" INTEGER NOT NULL DEFAULT 0, "OnBookFileDelete" INTEGER NOT NULL DEFAULT 0, "OnBookFileDeleteForUpgrade" INTEGER NOT NULL DEFAULT 0, "OnApplicationUpdate" INTEGER NOT NULL DEFAULT 1, "OnAuthorAdded" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "PendingReleases" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Title" TEXT NOT NULL, "Added" DATETIME NOT NULL, "Release" TEXT NOT NULL, "AuthorId" INTEGER NOT NULL, "ParsedBookInfo" TEXT NOT NULL, "Reason" INTEGER NOT NULL);

CREATE TABLE "QualityDefinitions" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Quality" INTEGER NOT NULL, "Title" TEXT NOT NULL, "MinSize" NUMERIC, "MaxSize" NUMERIC);

CREATE TABLE "QualityProfiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "Cutoff" INTEGER NOT NULL, "Items" TEXT NOT NULL, "UpgradeAllowed" INTEGER, "FormatItems" TEXT NOT NULL DEFAULT '[]', "MinFormatScore" INTEGER NOT NULL DEFAULT 0, "CutoffFormatScore" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "ReleaseProfiles" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Required" TEXT, "Ignored" TEXT, "Tags" TEXT NOT NULL, "Enabled" INTEGER NOT NULL, "IndexerId" INTEGER NOT NULL);

CREATE TABLE "RemotePathMappings" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Host" TEXT NOT NULL, "RemotePath" TEXT NOT NULL, "LocalPath" TEXT NOT NULL);

CREATE TABLE "RootFolders" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Path" TEXT NOT NULL, "Name" TEXT, "DefaultMetadataProfileId" INTEGER NOT NULL DEFAULT 0, "DefaultQualityProfileId" INTEGER NOT NULL DEFAULT 0, "DefaultMonitorOption" INTEGER NOT NULL DEFAULT 0, "DefaultTags" TEXT, "IsCalibreLibrary" INTEGER NOT NULL, "CalibreSettings" TEXT, "DefaultNewItemMonitorOption" INTEGER NOT NULL DEFAULT 0);

CREATE TABLE "ScheduledTasks" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "TypeName" TEXT NOT NULL, "Interval" INTEGER NOT NULL, "LastExecution" DATETIME NOT NULL, "LastStartTime" DATETIME);

CREATE TABLE "Series" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ForeignSeriesId" TEXT NOT NULL, "Title" TEXT NOT NULL, "Description" TEXT, "Numbered" INTEGER NOT NULL, "WorkCount" INTEGER NOT NULL, "PrimaryWorkCount" INTEGER NOT NULL);

CREATE TABLE "SeriesBookLink" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "SeriesId" INTEGER NOT NULL, "BookId" INTEGER NOT NULL, "Position" TEXT, "IsPrimary" INTEGER NOT NULL, "SeriesPosition" INTEGER NOT NULL DEFAULT 0, CONSTRAINT "FK_SeriesBookLink_SeriesId_Series_Id" FOREIGN KEY ("SeriesId") REFERENCES "Series" ("Id") ON DELETE CASCADE, CONSTRAINT "FK_SeriesBookLink_BookId_Books_Id" FOREIGN KEY ("BookId") REFERENCES "Books" ("Id") ON DELETE CASCADE);

CREATE TABLE "Tags" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Label" TEXT NOT NULL);

CREATE TABLE "Users" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Identifier" TEXT NOT NULL, "Username" TEXT NOT NULL, "Password" TEXT NOT NULL);

CREATE TABLE "VersionInfo" ("Version" INTEGER NOT NULL, "AppliedOn" DATETIME, "Description" TEXT);

CREATE UNIQUE INDEX "IX_AuthorMetadata_ForeignAuthorId" ON "AuthorMetadata" ("ForeignAuthorId" ASC);

CREATE UNIQUE INDEX "IX_AuthorMetadata_TitleSlug" ON "AuthorMetadata" ("TitleSlug" ASC);

CREATE UNIQUE INDEX "IX_Authors_AuthorMetadataId" ON "Authors" ("AuthorMetadataId" ASC);

CREATE INDEX "IX_Authors_CleanName" ON "Authors" ("CleanName" ASC);

CREATE INDEX "IX_Authors_Monitored" ON "Authors" ("Monitored" ASC);

CREATE INDEX "IX_Authors_Path" ON "Authors" ("Path" ASC);

CREATE INDEX "IX_BookFiles_EditionId" ON "BookFiles" ("EditionId" ASC);

CREATE UNIQUE INDEX "IX_BookFiles_Path" ON "BookFiles" ("Path" ASC);

CREATE INDEX "IX_Books_AuthorMetadataId" ON "Books" ("AuthorMetadataId" ASC);

CREATE INDEX "IX_Books_AuthorMetadataId_ReleaseDate" ON "Books" ("AuthorMetadataId" ASC, "ReleaseDate" ASC);

CREATE INDEX "IX_Books_CleanTitle" ON "Books" ("CleanTitle" ASC);

CREATE INDEX "IX_Books_ForeignBookId" ON "Books" ("ForeignBookId" ASC);

CREATE UNIQUE INDEX "IX_Books_TitleSlug" ON "Books" ("TitleSlug" ASC);

CREATE UNIQUE INDEX "IX_Config_Key" ON "Config" ("Key" ASC);

CREATE UNIQUE INDEX "IX_CustomFormats_Name" ON "CustomFormats" ("Name" ASC);

CREATE UNIQUE INDEX "IX_DownloadClientStatus_ProviderId" ON "DownloadClientStatus" ("ProviderId" ASC);

CREATE INDEX "IX_DownloadHistory_AuthorId" ON "DownloadHistory" ("AuthorId" ASC);

CREATE INDEX "IX_DownloadHistory_DownloadId" ON "DownloadHistory" ("DownloadId" ASC);

CREATE INDEX "IX_DownloadHistory_EventType" ON "DownloadHistory" ("EventType" ASC);

CREATE INDEX "IX_Editions_BookId" ON "Editions" ("BookId" ASC);

CREATE UNIQUE INDEX "IX_Editions_ForeignEditionId" ON "Editions" ("ForeignEditionId" ASC);

CREATE INDEX "IX_Editions_Monitored" ON "Editions" ("Monitored" ASC);

CREATE INDEX "IX_History_BookId_Date" ON "History" ("BookId" ASC, "Date" DESC);

CREATE INDEX "IX_History_Date" ON "History" ("Date" ASC);

CREATE INDEX "IX_History_DownloadId_Date" ON "History" ("DownloadId" ASC, "Date" DESC);

CREATE INDEX "IX_History_EventType" ON "History" ("EventType" ASC);

CREATE UNIQUE INDEX "IX_ImportListExclusions_ForeignId" ON "ImportListExclusions" ("ForeignId" ASC);

CREATE UNIQUE INDEX "IX_ImportListStatus_ProviderId" ON "ImportListStatus" ("ProviderId" ASC);

CREATE UNIQUE INDEX "IX_ImportLists_Name" ON "ImportLists" ("Name" ASC);

CREATE UNIQUE INDEX "IX_IndexerStatus_ProviderId" ON "IndexerStatus" ("ProviderId" ASC);

CREATE UNIQUE INDEX "IX_Indexers_Name" ON "Indexers" ("Name" ASC);

CREATE UNIQUE INDEX "IX_MetadataProfiles_Name" ON "MetadataProfiles" ("Name" ASC);

CREATE UNIQUE INDEX "IX_NotificationStatus_ProviderId" ON "NotificationStatus" ("ProviderId" ASC);

CREATE UNIQUE INDEX "IX_QualityDefinitions_Quality" ON "QualityDefinitions" ("Quality" ASC);

CREATE UNIQUE INDEX "IX_QualityDefinitions_Title" ON "QualityDefinitions" ("Title" ASC);

CREATE UNIQUE INDEX "IX_QualityProfiles_Name" ON "QualityProfiles" ("Name" ASC);

CREATE UNIQUE INDEX "IX_RootFolders_Path" ON "RootFolders" ("Path" ASC);

CREATE UNIQUE INDEX "IX_ScheduledTasks_TypeName" ON "ScheduledTasks" ("TypeName" ASC);

CREATE INDEX "IX_SeriesBookLink_SeriesId" ON "SeriesBookLink" ("SeriesId" ASC);

CREATE UNIQUE INDEX "IX_Series_ForeignSeriesId" ON "Series" ("ForeignSeriesId" ASC);

CREATE UNIQUE INDEX "IX_Tags_Label" ON "Tags" ("Label" ASC);

CREATE UNIQUE INDEX "IX_Users_Identifier" ON "Users" ("Identifier" ASC);

CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username" ASC);

CREATE UNIQUE INDEX "UC_Version" ON "VersionInfo" ("Version" ASC);
