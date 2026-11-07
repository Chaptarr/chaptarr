-- Shared populated BookshelfNG migration-158 fixture for importer tests and integration smoke.
INSERT INTO VersionInfo (Version, AppliedOn, Description)
                WITH RECURSIVE migrations(version) AS
                (
                    SELECT 1 UNION ALL SELECT version + 1 FROM migrations WHERE version < 158
                )
                SELECT version, CURRENT_TIMESTAMP, 'BookshelfNG schema fixture' FROM migrations;

                INSERT INTO QualityProfiles (Id, Name, Cutoff, Items, UpgradeAllowed)
                VALUES (1, 'Audio default', 1, '[{"quality":1,"allowed":true}]', 1),
                       (2, 'Ebook override', 2, '[{"quality":2,"allowed":true}]', 0);
                INSERT INTO MetadataProfiles (Id, Name, MinPopularity, SkipMissingDate, SkipMissingIsbn, SkipPartsAndSets, SkipSeriesSecondary, AllowedLanguages, PreferredEditionTerms, PreferredSeries)
                VALUES
                    (1, 'Audio metadata', 0, 0, 0, 0, 0, '[]', '3|audible', 'The Fixture Series'),
                    (2, 'Ebook metadata', 0, 0, 0, 0, 0, '[]', '2|epub', 'Fixture Ebooks');
                INSERT INTO RootFolders (Id, Path, Name, DefaultMetadataProfileId, DefaultQualityProfileId, DefaultMonitorOption, DefaultTags, IsCalibreLibrary, CalibreSettings, DefaultNewItemMonitorOption)
                VALUES (1, '/library', 'Library', 1, 1, 0, '[]', 0, NULL, 2);
                INSERT INTO Config (Id, Key, Value) VALUES (1, 'FixtureSetting', 'preserved');
                INSERT INTO DownloadClients (Id, Enable, Name, Implementation, Settings, ConfigContract, Priority, Tags, RemoveCompletedDownloads, RemoveFailedDownloads)
                VALUES (109, 1, 'qBittorrent fixture', 'QBittorrent', '{"host":"localhost","port":8080,"audiobookCategory":"legacy-audio","ebookCategory":"legacy-ebooks"}', 'QBittorrentSettings', 1, '[]', 1, 1);
                INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                VALUES (110, 'Fixture list', 'GoodreadsBookshelf', '{"userId":"12345678","bookshelfIds":["1"]}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 0, 1, 1, '[]');
                INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                VALUES (120, 'Fixture indexer', 'MyAnonaMouse', '{"baseUrl":"https://www.myanonamouse.net","mamId":"fixture-token","searchType":2,"searchInDescription":true,"searchInSeries":false,"searchInFilenames":true,"searchLanguages":[4,1],"useFreeleechWedge":1,"seedCriteria":{"seedRatio":1.5,"seedTime":4320,"discographySeedTime":10080,"neverMoveOnImport":true},"rejectBlocklistedTorrentHashesWhileGrabbing":true}', 'MyAnonaMouseSettings', 1, 1, 1, 25, '[]', 0);
                INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                VALUES (121, 'Direct fixture', 'DirectDownloadIndexer', '{"urls": ["https://primary.example/", "https://secondary.example"], "apiKey": "fixture-key", "enableSlowFallback": true}', 'DirectDownloadSettings', 0, 1, 1, 25, '[]', 0);
                INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                VALUES (122, 'Newznab fixture', 'Newznab', '{"baseUrl":"https://indexer.example","apiKey":"fixture-key"}', 'NewznabSettings', 0, 1, 1, 25, '[]', 0);
                INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                VALUES (111, 'Fixture notification', 1, '{"baseUrl":"http://booklore.example","username":"fixture","password":"fixture-password"}', 'BookLore', 'BookLoreSettings', 1, '[]', 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0);
                INSERT INTO Users (Id, Identifier, Username, Password)
                VALUES (112, '00000000-0000-0000-0000-000000000020', 'fixture-user', 'legacy-sha256-hash');
                INSERT INTO AuthorMetadata (Id, ForeignAuthorId, TitleSlug, Name, Overview, Status, Images, Aliases, SortName, NameLastFirst, SortNameLastFirst)
                VALUES (10, 'hardcover-author:author-10', 'hardcover-author:author-10', 'Fixture Author', 'Fixture overview', 0, '[]', '[]', 'Fixture Author', 'Author, Fixture', 'Author, Fixture');
                INSERT INTO Authors (Id, CleanName, Path, Monitored, QualityProfileId, Tags, Added, MetadataProfileId, AuthorMetadataId, MonitorNewItems, EbookPath, AudiobookPath, EbookQualityProfileId, AudiobookQualityProfileId, EbookMetadataProfileId, AudiobookMetadataProfileId)
                VALUES (20, 'fixtureauthor', '/library/Fixture Author', 1, 1, '[]', '2026-01-01', 1, 10, 2, '/library/Fixture Author/Ebooks', '/library/Fixture Author/Audiobooks', 2, 1, 2, 1);
                INSERT INTO Books (Id, AuthorMetadataId, ForeignBookId, TitleSlug, Title, Links, Genres, Ratings, CleanTitle, Monitored, AnyEditionOk, Added, RelatedBooks, LastSearchTime)
                VALUES (30, 10, 'hardcover:book-30', 'hardcover:book-30', 'Fixture Book', '[]', '[]', '{}', 'fixture book', 1, 0, '2026-01-02', '[]', NULL);
                INSERT INTO Editions (Id, BookId, ForeignEditionId, Isbn13, Asin, Title, TitleSlug, Images, Links, IsEbook, Monitored, ManualAdd, Narrator)
                VALUES
                    (40, 30, 'hardcover-edition:audio-40', NULL, NULL, 'Fixture Audiobook', 'audio-40', '[{"Url":"https://covers.example/audio.jpg","CoverType":6}]', '[{"Url":"https://catalog.example/audio-40","Name":"Catalog"}]', 0, 1, 0, 'A Narrator'),
                    (41, 30, 'openlibrary-edition:OL123M', '9780000000001', NULL, 'Fixture Ebook', 'OL123M', '[]', NULL, 1, 0, 0, NULL);
                INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Modified, Path, Part, LastRemoteSearchTime)
                VALUES
                    (50, 40, 0, '{}', 100, '2026-01-02', '{}', '2026-01-02', '/library/Fixture Author/book.m4b', 0, '2026-01-04T05:06:07Z'),
                    (51, 41, 0, '{}', 200, '2026-01-03', '{}', '2026-01-03', '/library/Fixture Author/book.epub', 0, NULL);
                INSERT INTO ExtraFiles (Id, AuthorId, BookId, BookFileId, RelativePath, Extension, Added, LastUpdated)
                VALUES (60, 20, 30, 999, 'cover.jpg', 'jpg', '2026-01-03', '2026-01-03');
                INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                VALUES (61, 20, 'AudioBookShelf', 0, 'metadata.opf', '2026-01-03', 30, 51, 'opf');
                INSERT INTO Series (Id, ForeignSeriesId, Title, Numbered, WorkCount, PrimaryWorkCount)
                VALUES (70, 'hardcover-series:series-70', 'Fixture Series', 1, 1, 1);
                INSERT INTO SeriesBookLink (Id, SeriesId, BookId, Position, IsPrimary, SeriesPosition)
                VALUES (80, 70, 30, '1', 1, 1);
                INSERT INTO Blocklist (Id, SourceTitle, Quality, Date, AuthorId, BookIds, IndexerFlags)
                VALUES (92, 'Fixture release', '{}', '2026-01-04', 20, '[30]', 0);
                INSERT INTO DownloadHistory (Id, EventType, AuthorId, DownloadId, SourceTitle, Date, Data)
                VALUES (91, 1, 20, 'download-91', 'Fixture release', '2026-01-04', '{}');
                INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                VALUES (90, 'Fixture release', '2026-01-04', '{}', '{}', 1, 'download-91', 20, 30);
                INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                VALUES (93, 'book.epub', '2026-01-04', '{}', '{"FileId":"51","ImportedPath":"/library/Fixture Author/book.epub"}', 3, 'download-91', 20, 30);
                INSERT INTO Commands (Id, Name, Body, Priority, Status, QueuedAt, StartedAt, EndedAt, Duration, Trigger)
                VALUES (100, 'OldCommand', '{}', 1, 2, '2026-01-04', '2026-01-04', '2026-01-04', '00:00:00', 0);
