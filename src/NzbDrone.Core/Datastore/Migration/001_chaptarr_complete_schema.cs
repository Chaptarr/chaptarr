using System.Collections.Generic;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(1)]
    public class chaptarr_complete_schema : NzbDroneMigrationBase
    {
        // BASELINE NOTE:
        // This migration is intended to be the authoritative, baseline schema for fresh installs.
        // It should reflect the complete schema as produced by prior incremental migrations (001–031)
        // at commit <set via scripts/sqlite_dump.sh>. Do not hand-edit casually.
        // Provenance: baseline generated from full migration chain; see baseline_sqlite.sql and checksum.
        protected override void MainDbUpgrade()
        {
            // Log schema version
            Execute.Sql("SELECT 'Chaptarr complete schema v1.0' AS schema_version");

            CreateCoreTables();
            CreateAuthorTables();
            CreateBookTables();
            CreateEditionTables();
            CreateSeriesTables();
            CreateNarratorTables();
            CreateMediaFilesTables();
            CreateImportListTables();
            CreateImportWorkflowTables();
            CreateQualityTables();
            CreateIndexerTables();
            CreateNotificationTables();
            CreateNotificationStatusTables();
            CreateApplicationTables();
            CreateIntegrationTables();
            CreateUpdateHistoryTables();
            CreateFuzzyMatchingTables();
            CreateFtsSearchTables();
            CreateIndexes();
            CreateConstraints();
            InsertDefaultData();
        }

        protected override void LogDbUpgrade()
        {
            // Create Logs table in the separate logs database
            Create.TableForModel("Logs")
                .WithColumn("Message").AsString().NotNullable()
                .WithColumn("Time").AsDateTime().NotNullable()
                .WithColumn("Logger").AsString().NotNullable()
                .WithColumn("Exception").AsString().Nullable()
                .WithColumn("ExceptionType").AsString().Nullable()
                .WithColumn("Level").AsString().NotNullable();

            // Create index for faster log queries
            Create.Index().OnTable("Logs").OnColumn("Time").Descending();
            Create.Index().OnTable("Logs").OnColumn("Level");
        }

        private void CreateCoreTables()
        {
            // Root folders (final: no AcceptsMixedContent; no generic Default* columns; add per-media settings JSON)
            Create.TableForModel("RootFolders")
                .WithColumn("Path").AsString().NotNullable().Unique()
                .WithColumn("Name").AsString().Nullable()
                .WithColumn("DefaultSearchCriteriaProfileId").AsInt32().Nullable()
                .WithColumn("DefaultTags").AsString().Nullable()
                .WithColumn("IsCalibreLibrary").AsBoolean().WithDefaultValue(false)
                .WithColumn("CalibreSettings").AsString().Nullable()
                .WithColumn("UseCalibreNaming").AsBoolean().WithDefaultValue(false)
                .WithColumn("OutputFormat").AsString().Nullable()
                .WithColumn("OutputProfile").AsString().Nullable()
                .WithColumn("FolderType").AsInt32().NotNullable().WithDefaultValue(0) // 0=Mixed,1=Audiobook,2=Ebook
                .WithColumn("AudiobookSettings").AsString().Nullable()
                .WithColumn("EbookSettings").AsString().Nullable();

            // Naming configuration
            Create.TableForModel("NamingConfig")
                .WithColumn("MultiEpisodeStyle").AsInt32()
                .WithColumn("ReplaceIllegalCharacters").AsBoolean().WithDefaultValue(true)
                .WithColumn("StandardBookFormat").AsString()
                .WithColumn("AuthorFolderFormat").AsString()
                .WithColumn("RenameBooks").AsBoolean()
                .WithColumn("ColonReplacementFormat").AsInt32().NotNullable().WithDefaultValue(0);

            // Configuration
            Create.TableForModel("Config")
                .WithColumn("Key").AsString().NotNullable().Unique()
                .WithColumn("Value").AsString().NotNullable();

            // Scheduled tasks
            Create.TableForModel("ScheduledTasks")
                .WithColumn("TypeName").AsString().NotNullable()
                .WithColumn("Interval").AsDouble().NotNullable()
                .WithColumn("LastExecution").AsDateTime().NotNullable()
                .WithColumn("LastDuration").AsDateTime().Nullable()
                .WithColumn("LastStartTime").AsDateTime().Nullable();

            // User persistence
            Create.TableForModel("Users")
                .WithColumn("Identifier").AsString().NotNullable().Unique()
                .WithColumn("Username").AsString().NotNullable().Unique()
                .WithColumn("Password").AsString().NotNullable()
                .WithColumn("Salt").AsString().Nullable()
                .WithColumn("Iterations").AsInt32().Nullable();

            // Commands
            Create.TableForModel("Commands")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Body").AsString().NotNullable()
                .WithColumn("Priority").AsInt32().NotNullable()
                .WithColumn("Status").AsInt32().NotNullable()
                .WithColumn("Result").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("QueuedAt").AsDateTime().NotNullable()
                .WithColumn("StartedAt").AsDateTime().Nullable()
                .WithColumn("EndedAt").AsDateTime().Nullable()
                .WithColumn("Duration").AsString().Nullable()
                .WithColumn("Exception").AsString().Nullable()
                .WithColumn("Trigger").AsInt32().NotNullable()
                .WithColumn("Message").AsString().Nullable()
                // Watchdog/heartbeat support
                .WithColumn("LastProgressAt").AsDateTime().Nullable();

            // History
            Create.TableForModel("History")
                .WithColumn("BookId").AsInt32()
                .WithColumn("Quality").AsString().NotNullable()
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("EventType").AsInt32().Nullable()
                .WithColumn("Data").AsString().Nullable()
                .WithColumn("DownloadId").AsString().Nullable()
                .WithColumn("SourceTitle").AsString().NotNullable()
                .WithColumn("AuthorId").AsInt32().Nullable()
                .WithColumn("EditionId").AsInt32().Nullable();

            // Download clients
            Create.TableForModel("DownloadClients")
                .WithColumn("Enable").AsBoolean().NotNullable()
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("Settings").AsString().Nullable()
                .WithColumn("ConfigContract").AsString().NotNullable()
                .WithColumn("Priority").AsInt32().WithDefaultValue(1)
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("RemoveCompletedDownloads").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("RemoveFailedDownloads").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("Protocol").AsInt32().Nullable();

            // Pending releases
            Create.TableForModel("PendingReleases")
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("Title").AsString().NotNullable()
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("Release").AsString().NotNullable()
                .WithColumn("ParsedBookInfo").AsString().Nullable()
                .WithColumn("Reason").AsInt32().NotNullable()
                .WithColumn("AdditionalInfo").AsString().Nullable();

            // Release profiles
            Create.TableForModel("ReleaseProfiles")
                .WithColumn("Name").AsString().Nullable()
                .WithColumn("Enabled").AsBoolean().NotNullable()
                .WithColumn("Required").AsString().Nullable()
                .WithColumn("Ignored").AsString().Nullable()
                .WithColumn("IndexerId").AsInt32().WithDefaultValue(0)
                .WithColumn("Tags").AsString().NotNullable();

            // Remote path mappings
            Create.TableForModel("RemotePathMappings")
                .WithColumn("DownloadClientId").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Host").AsString().NotNullable()
                .WithColumn("RemotePath").AsString().NotNullable()
                .WithColumn("LocalPath").AsString().NotNullable();

            // Tags
            Create.TableForModel("Tags")
                .WithColumn("Label").AsString().NotNullable().Unique();

            // Proxies
            // Columns aligned with ProxyDefinition model properties
            Create.TableForModel("Proxies")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("ProxyType").AsInt32().NotNullable()
                .WithColumn("Hostname").AsString().Nullable()
                .WithColumn("Port").AsInt32().Nullable()
                .WithColumn("Username").AsString().Nullable()
                .WithColumn("Password").AsString().Nullable()
                .WithColumn("BypassFilter").AsString().Nullable()
                .WithColumn("BypassLocalAddresses").AsBoolean().NotNullable().WithDefaultValue(true);

            // Restrictions
            Create.TableForModel("Restrictions")
                .WithColumn("Name").AsString().Nullable()
                .WithColumn("Required").AsString().Nullable()
                .WithColumn("Preferred").AsString().Nullable()
                .WithColumn("Ignored").AsString().Nullable()
                .WithColumn("Tags").AsString().NotNullable();

            // Delay profiles
            Create.TableForModel("DelayProfiles")
                .WithColumn("EnableUsenet").AsBoolean().NotNullable()
                .WithColumn("EnableTorrent").AsBoolean().NotNullable()
                .WithColumn("PreferredProtocol").AsInt32().NotNullable()
                .WithColumn("UsenetDelay").AsInt32().NotNullable()
                .WithColumn("TorrentDelay").AsInt32().NotNullable()
                .WithColumn("Order").AsInt32().NotNullable()
                .WithColumn("Tags").AsString().NotNullable()
                .WithColumn("BypassIfHighestQuality").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("BypassIfAboveCustomFormatScore").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("MinimumCustomFormatScore").AsInt32().Nullable();

            // Custom filters
            Create.TableForModel("CustomFilters")
                .WithColumn("Type").AsString().NotNullable()
                .WithColumn("Label").AsString().NotNullable()
                .WithColumn("Filters").AsString().NotNullable();
        }

        private void CreateAuthorTables()
        {
            // Authors (merged metadata + management)
            Create.TableForModel("Authors")
                // Metadata fields (merged)
                .WithColumn("TitleSlug").AsString().Nullable()
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("SortName").AsString().Nullable()
                .WithColumn("NameLastFirst").AsString().Nullable()
                .WithColumn("SortNameLastFirst").AsString().Nullable()
                .WithColumn("Aliases").AsString().WithDefaultValue("[]")
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("Disambiguation").AsString().Nullable()
                .WithColumn("Gender").AsString().Nullable()
                .WithColumn("Hometown").AsString().Nullable()
                .WithColumn("Born").AsDateTime().Nullable()
                .WithColumn("Died").AsDateTime().Nullable()
                .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Images").AsString().WithDefaultValue("[]")
                .WithColumn("Links").AsString().WithDefaultValue("[]")
                .WithColumn("Genres").AsString().WithDefaultValue("[]")
                .WithColumn("Ratings").AsString().Nullable()
                .WithColumn("GoodreadsAuthorId").AsString().Nullable()
                .WithColumn("HardcoverAuthorId").AsString().Nullable()
                .WithColumn("AudnexusAuthorId").AsString().Nullable()
                .WithColumn("OpenLibraryAuthorId").AsString().Nullable()
                .WithColumn("GoogleBooksAuthorId").AsString().Nullable()
                .WithColumn("LibraryThingAuthorId").AsString().Nullable()
                .WithColumn("ISNI").AsString().Nullable()
                .WithColumn("VIAF").AsString().Nullable()
                .WithColumn("Pseudonyms").AsString().WithDefaultValue("[]")
                .WithColumn("ProviderUrls").AsString().WithDefaultValue("{}")
                .WithColumn("LastUpdated").AsDateTime().Nullable()
                // Management fields
                .WithColumn("Path").AsString().NotNullable()
                .WithColumn("Monitored").AsBoolean().NotNullable()
                .WithColumn("MonitorNewItems").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("QualityProfileId").AsInt32().NotNullable().WithDefaultValue(1)
                .WithColumn("MetadataProfileId").AsInt32().Nullable()
                .WithColumn("AudiobookQualityProfileId").AsInt32().Nullable()
                .WithColumn("AudiobookMetadataProfileId").AsInt32().Nullable()
                .WithColumn("EbookQualityProfileId").AsInt32().Nullable()
                .WithColumn("EbookMetadataProfileId").AsInt32().Nullable()
                .WithColumn("AudiobookRootFolderPath").AsString().Nullable()
                .WithColumn("EbookRootFolderPath").AsString().Nullable()
                // Per-media resolved paths (added to baseline; existed in incremental migrations)
                .WithColumn("AudiobookPath").AsString().Nullable()
                .WithColumn("EbookPath").AsString().Nullable()
                .WithColumn("AudiobookMonitorExisting").AsInt32().Nullable()
                .WithColumn("AudiobookMonitorFuture").AsBoolean().Nullable()
                .WithColumn("EbookMonitorExisting").AsInt32().Nullable()
                .WithColumn("EbookMonitorFuture").AsBoolean().Nullable()
                .WithColumn("AudiobookSettingsManuallyOverridden").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("EbookSettingsManuallyOverridden").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("LastSelectedMediaType").AsString().NotNullable().WithDefaultValue("audiobook")
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("AddOptions").AsString().Nullable()
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("LastInfoSync").AsDateTime().Nullable()
                .WithColumn("CleanName").AsString().NotNullable()
                .WithColumn("SelectedPosterHash").AsString().Nullable();
        }

        private void CreateBookTables()
        {
            // Books (final: reference Authors via AuthorId; no Monitored; include BaseBookId)
            Create.TableForModel("Books")
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("ForeignEditionId").AsString().Nullable()
                .WithColumn("Title").AsString().NotNullable()
                .WithColumn("Subtitle").AsString().Nullable()
                .WithColumn("OriginalTitle").AsString().Nullable()
                .WithColumn("CleanTitle").AsString().NotNullable()
                .WithColumn("SortTitle").AsString().Nullable()
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("AudiobookMonitored").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("EbookMonitored").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("AnyEditionOk").AsBoolean().NotNullable()
                .WithColumn("LastInfoSync").AsDateTime().Nullable()
                .WithColumn("LastDiskSync").AsDateTime().Nullable()
                .WithColumn("LastSearchTime").AsDateTime().Nullable()
                .WithColumn("ReleaseDate").AsDateTime().Nullable()
                .WithColumn("Ratings").AsString().Nullable()
                .WithColumn("Genres").AsString().WithDefaultValue("[]")
                .WithColumn("Links").AsString().WithDefaultValue("[]")
                .WithColumn("RelatedBooks").AsString().WithDefaultValue("[]")
                .WithColumn("Images").AsString().WithDefaultValue("[]")
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("AddOptions").AsString().Nullable()
                .WithColumn("BaseBookId").AsString().Nullable()
                .WithColumn("GoodreadsBookId").AsString().Nullable()
                .WithColumn("GoodreadsWorkId").AsString().Nullable()
                .WithColumn("HardcoverBookId").AsString().Nullable()
                .WithColumn("ISBN10").AsString().Nullable()
                .WithColumn("ISBN13").AsString().Nullable()
                .WithColumn("OpenLibraryEditionId").AsString().Nullable()
                .WithColumn("OpenLibraryWorkId").AsString().Nullable()
                .WithColumn("GoogleBooksId").AsString().Nullable()
                .WithColumn("LibraryThingId").AsString().Nullable()
                .WithColumn("ASIN").AsString().Nullable()
                .WithColumn("AudibleASIN").AsString().Nullable()
                .WithColumn("LanguageCode").AsString().Nullable()
                .WithColumn("LanguageName").AsString().Nullable()
                .WithColumn("PublicationYear").AsInt32().Nullable()
                .WithColumn("Publisher").AsString().Nullable()
                .WithColumn("PageCount").AsInt32().Nullable()
                .WithColumn("SeriesId").AsInt32().Nullable()
                .WithColumn("SeriesName").AsString().Nullable()
                .WithColumn("SeriesPosition").AsString().Nullable()
                .WithColumn("IsGraphicAudio").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("AudioProductionType").AsString().Nullable()
                .WithColumn("NarratorId").AsInt32().Nullable()
                .WithColumn("Narrator").AsString().Nullable()
                .WithColumn("InstanceType").AsString().NotNullable().WithDefaultValue("physical")
                .WithColumn("WantedNarratorId").AsInt32().Nullable()
                .WithColumn("DurationMinutes").AsInt32().Nullable()
                .WithColumn("MediaType").AsInt32().WithDefaultValue(0)
                .WithColumn("TitleSlug").AsString().Nullable()
                .WithColumn("ExpandedTitles").AsString().Nullable()
                .WithColumn("ProviderUrls").AsString().WithDefaultValue("{}")
                .WithColumn("IsOmnibus").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("LastUpdated").AsDateTime().Nullable();

            // Book-related indexes
            Create.Index("IX_Books_BaseBookId").OnTable("Books").OnColumn("BaseBookId");
            Create.Index("IX_Books_MediaType_AudiobookMonitored")
                .OnTable("Books")
                .OnColumn("MediaType").Ascending()
                .OnColumn("AudiobookMonitored").Ascending()
                .OnColumn("AuthorId").Ascending();
            Create.Index("IX_Books_MediaType_EbookMonitored")
                .OnTable("Books")
                .OnColumn("MediaType").Ascending()
                .OnColumn("EbookMonitored").Ascending()
                .OnColumn("AuthorId").Ascending();
        }

        private void CreateEditionTables()
        {
            // Editions (per the-import-discussion.md)
            Create.TableForModel("Editions")
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("ForeignEditionId").AsString().Nullable()
                .WithColumn("Title").AsString().NotNullable()
                .WithColumn("TitleSlug").AsString().Nullable()
                .WithColumn("Isbn13").AsString().Nullable()
                .WithColumn("Isbn10").AsString().Nullable()
                .WithColumn("Asin").AsString().Nullable()
                .WithColumn("Language").AsString().Nullable()
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("Format").AsString().Nullable()
                .WithColumn("IsEbook").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("Disambiguation").AsString().Nullable()
                .WithColumn("Publisher").AsString().Nullable()
                .WithColumn("PageCount").AsInt32().Nullable()
                .WithColumn("DurationSeconds").AsInt32().Nullable()
                .WithColumn("ChapterCount").AsInt32().Nullable()
                .WithColumn("HasChapters").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("ReleaseDate").AsDateTime().Nullable()
                .WithColumn("Monitored").AsBoolean().NotNullable()
                .WithColumn("ManualAdd").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("IsFallbackEdition").AsBoolean().WithDefaultValue(false)
                .WithColumn("Images").AsString().WithDefaultValue("[]")
                .WithColumn("Links").AsString().WithDefaultValue("[]")
                .WithColumn("Ratings").AsString().Nullable()
                .WithColumn("GoodreadsEditionId").AsInt64().Nullable()
                .WithColumn("HardcoverEditionId").AsString().Nullable()
                .WithColumn("OpenLibraryEditionId").AsString().Nullable()
                .WithColumn("AudibleASIN").AsString().Nullable()
                .WithColumn("GoogleBooksEditionId").AsString().Nullable()
                .WithColumn("LibraryThingEditionId").AsString().Nullable()
                .WithColumn("ReadingFormatId").AsInt32().Nullable()
                .WithColumn("EditionFormat").AsString().Nullable()
                .WithColumn("EditionInfo").AsString().Nullable()
                .WithColumn("IsGraphicAudio").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("AudioProductionType").AsString().Nullable()
                .WithColumn("Narrator").AsString().Nullable()
                .WithColumn("NarratorNames").AsString().WithDefaultValue("[]")
                .WithColumn("ProviderUrls").AsString().WithDefaultValue("{}")
                .WithColumn("LastUpdated").AsDateTime().Nullable()
                .WithColumn("MediaType").AsInt32().NotNullable().WithDefaultValue(0);

            // Editions indexes are defined in CreateIndexes()
        }

        private void CreateSeriesTables()
        {
            // Series (multi-copy architecture)
            Create.TableForModel("Series")
                .WithColumn("Title").AsString().NotNullable()
                .WithColumn("TitleSlug").AsString().Nullable()
                .WithColumn("Description").AsString().Nullable()
                .WithColumn("Numbered").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("WorkCount").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("PrimaryWorkCount").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("SeriesType").AsString().Nullable()
                .WithColumn("ParentSeriesId").AsInt32().Nullable()
                .WithColumn("TotalBooks").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("PrimaryBooks").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("BaseSeriesId").AsString().Nullable()
                .WithColumn("InstanceType").AsString().NotNullable().WithDefaultValue("original")
                .WithColumn("InstanceNumber").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("PreferredNarratorId").AsInt32().Nullable()
                .WithColumn("Narrator").AsString().Nullable()
                .WithColumn("GoodreadsSeriesId").AsString().Nullable()
                .WithColumn("HardcoverSeriesId").AsString().Nullable()
                .WithColumn("OpenLibrarySeriesId").AsString().Nullable()
                .WithColumn("Links").AsString().WithDefaultValue("{}")
                .WithColumn("ProviderUrls").AsString().WithDefaultValue("{}")
                .WithColumn("LastUpdated").AsDateTime().Nullable();

            // Series to book links
            Create.TableForModel("SeriesBookLink")
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("Position").AsString().Nullable()
                .WithColumn("SeriesPosition").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("IsPrimary").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SeriesInstanceType").AsString().NotNullable().WithDefaultValue("original")
                .WithColumn("IsInheritedLink").AsBoolean().NotNullable().WithDefaultValue(false);

            // Series to author links (final: use AuthorId)
            Create.TableForModel("AuthorSeries")
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("AuthorId").AsInt32().NotNullable();
        }

        private void CreateNarratorTables()
        {
            // Narrator metadata
            // Narrator metadata (shared between narrators)
            Create.TableForModel("NarratorMetadata")
                .WithColumn("LocalNarratorId").AsString().Nullable()
                .WithColumn("TitleSlug").AsString().Nullable()
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("SortName").AsString().Nullable()
                .WithColumn("NameLastFirst").AsString().Nullable()
                .WithColumn("SortNameLastFirst").AsString().Nullable()
                .WithColumn("Aliases").AsString().WithDefaultValue("[]")
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("Disambiguation").AsString().Nullable()
                .WithColumn("Gender").AsString().Nullable()
                .WithColumn("Hometown").AsString().Nullable()
                .WithColumn("Born").AsDateTime().Nullable()
                .WithColumn("Died").AsDateTime().Nullable()
                .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Images").AsString().WithDefaultValue("[]")
                .WithColumn("Links").AsString().WithDefaultValue("[]")
                .WithColumn("Genres").AsString().WithDefaultValue("[]")
                .WithColumn("Ratings").AsString().Nullable()
                .WithColumn("HardcoverNarratorId").AsString().Nullable()
                .WithColumn("GoodreadsNarratorId").AsString().Nullable()
                .WithColumn("LastInfoSync").AsDateTime().Nullable();

            // Narrators (local instance)
            Create.TableForModel("Narrators")
                .WithColumn("NarratorMetadataId").AsInt32().NotNullable()
                .WithColumn("CleanName").AsString().Nullable()
                .WithColumn("Monitored").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("MonitorNewItems").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("LastInfoSync").AsDateTime().Nullable()
                .WithColumn("Path").AsString().Nullable()
                .WithColumn("RootFolderPath").AsString().Nullable()
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("Tags").AsString().WithDefaultValue("[]");

            // Book to narrator links
            Create.TableForModel("BookNarratorLink")
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("NarratorId").AsInt32().NotNullable()
                .WithColumn("IsPrimary").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("Role").AsString().NotNullable().WithDefaultValue("Narrator");

            // Edition to narrator links
            Create.TableForModel("EditionNarratorLink")
                .WithColumn("EditionId").AsInt32().NotNullable()
                .WithColumn("NarratorId").AsInt32().NotNullable()
                .WithColumn("IsPrimary").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("Role").AsString().NotNullable().WithDefaultValue("Narrator");
        }

        private void CreateMediaFilesTables()
        {
            // Book files
            Create.TableForModel("BookFiles")
                .WithColumn("Path").AsString().NotNullable()
                .WithColumn("Size").AsInt64().NotNullable()
                .WithColumn("Modified").AsDateTime().NotNullable()
                .WithColumn("DateAdded").AsDateTime().NotNullable()
                .WithColumn("OriginalFilePath").AsString().Nullable()
                .WithColumn("SceneName").AsString().Nullable()
                .WithColumn("ReleaseGroup").AsString().Nullable()
                .WithColumn("Quality").AsString().NotNullable()
                .WithColumn("IndexerFlags").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("MediaInfo").AsString().NotNullable()
                .WithColumn("EditionId").AsInt32().NotNullable()
                .WithColumn("CalibreId").AsInt32().NotNullable()
                .WithColumn("Part").AsInt32().NotNullable()
                .WithColumn("IsGraphicAudio").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("AudioProductionType").AsString().Nullable()
                .WithColumn("Narrator").AsString().Nullable()
                .WithColumn("MatchConfidence").AsDouble().Nullable()
                .WithColumn("LastMatchAttempt").AsDateTime().Nullable()
                .WithColumn("MatchDetails").AsString().Nullable()
                .WithColumn("MediaType").AsString().NotNullable().WithDefaultValue("audiobook")
                .WithColumn("UnmappedReason").AsString().Nullable()
                .WithColumn("Author").AsString().Nullable()
                .WithColumn("Edition").AsString().Nullable();

            // Metadata files
            Create.TableForModel("MetadataFiles")
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("Consumer").AsString().NotNullable()
                .WithColumn("Type").AsInt32().NotNullable()
                .WithColumn("RelativePath").AsString().NotNullable()
                .WithColumn("LastUpdated").AsDateTime().NotNullable()
                .WithColumn("BookId").AsInt32().Nullable()
                .WithColumn("Added").AsDateTime().Nullable()
                .WithColumn("Extension").AsString().NotNullable()
                .WithColumn("Hash").AsString().Nullable()
                .WithColumn("BookFileId").AsInt32().Nullable();

            // Other files
            Create.TableForModel("OtherFiles")
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("EditionId").AsInt32().Nullable()
                .WithColumn("RelativePath").AsString().NotNullable()
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("LastUpdated").AsDateTime().NotNullable()
                .WithColumn("Extension").AsString().NotNullable();

            // Import list exclusions (final: use provider ForeignId)
            Create.TableForModel("ImportListExclusions")
                .WithColumn("ForeignId").AsString().NotNullable()
                .WithColumn("Name").AsString().NotNullable();

            // Import list status
            Create.TableForModel("ImportListStatus")
                .WithColumn("ProviderId").AsInt32().NotNullable().Unique()
                .WithColumn("InitialFailure").AsDateTime().Nullable()
                .WithColumn("MostRecentFailure").AsDateTime().Nullable()
                .WithColumn("EscalationLevel").AsInt32().NotNullable()
                .WithColumn("DisabledTill").AsDateTime().Nullable()
                .WithColumn("LastInfoSync").AsDateTime().Nullable();

            // Downloaded books reporting
            Create.TableForModel("DownloadHistory")
                .WithColumn("EventType").AsInt32().NotNullable()
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("DownloadId").AsString().NotNullable()
                .WithColumn("SourceTitle").AsString().NotNullable()
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("Protocol").AsInt32().Nullable()
                .WithColumn("IndexerId").AsInt32().Nullable()
                .WithColumn("DownloadClientId").AsInt32().Nullable()
                .WithColumn("Release").AsString().Nullable()
                .WithColumn("Data").AsString().Nullable();

            // Download client file snapshots
            Create.Table("DownloadClientFileSnapshots")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("DownloadClientId").AsInt32().NotNullable()
                .WithColumn("DownloadId").AsString().NotNullable()
                .WithColumn("Protocol").AsInt32().NotNullable()
                .WithColumn("Title").AsString().Nullable()
                .WithColumn("Category").AsString().Nullable()
                .WithColumn("OutputPath").AsString().Nullable()
                .WithColumn("Source").AsString(32).NotNullable()
                .WithColumn("Confidence").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("FilePaths").AsString().NotNullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("LastUpdated").AsDateTime().NotNullable();

            // Extra files
            Create.TableForModel("ExtraFiles")
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("BookFileId").AsInt32().NotNullable()
                .WithColumn("RelativePath").AsString().NotNullable()
                .WithColumn("Extension").AsString().NotNullable()
                .WithColumn("Added").AsDateTime().NotNullable()
                .WithColumn("LastUpdated").AsDateTime().NotNullable();

            // Blocklist (current table name)
            // ONLY stores provider-prefixed IDs to avoid collisions (e.g., "hc:123", "gr:456", "ol:789")
            Create.TableForModel("Blocklist")
                .WithColumn("SourceTitle").AsString().NotNullable()
                .WithColumn("Quality").AsString().NotNullable()
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("PublishedDate").AsDateTime().Nullable()
                .WithColumn("Size").AsInt64().Nullable()
                .WithColumn("Protocol").AsInt32().Nullable()
                .WithColumn("Indexer").AsString().Nullable()
                .WithColumn("IndexerFlags").AsInt32().Nullable()
                .WithColumn("Message").AsString().Nullable()
                .WithColumn("TorrentInfoHash").AsString().Nullable()
                .WithColumn("AuthorProviderIds").AsString().NotNullable() // JSON array: ["hc:123", "gr:456"]
                .WithColumn("BookProviderIds").AsString().NotNullable(); // JSON array: ["hc:789", "gr:012", "ol:345"]

            // Blacklist (legacy table name for compatibility - will be migrated to Blocklist)
            Create.TableForModel("Blacklist")
                .WithColumn("SourceTitle").AsString().NotNullable()
                .WithColumn("Quality").AsString().NotNullable()
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("PublishedDate").AsDateTime().Nullable()
                .WithColumn("Size").AsInt64().Nullable()
                .WithColumn("Protocol").AsInt32().Nullable()
                .WithColumn("Indexer").AsString().Nullable()
                .WithColumn("IndexerFlags").AsInt32().Nullable()
                .WithColumn("Message").AsString().Nullable()
                .WithColumn("TorrentInfoHash").AsString().Nullable()
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("BookIds").AsString().NotNullable();
        }

        private void CreateImportListTables()
        {
            // Import lists
            Create.TableForModel("ImportLists")
                .WithColumn("EnableAutomaticAdd").AsBoolean().NotNullable()
                .WithColumn("ShouldMonitor").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("ShouldSearch").AsBoolean().NotNullable()
                .WithColumn("ShouldMonitorExisting").AsBoolean().NotNullable()
                .WithColumn("MonitorNewItems").AsInt32().NotNullable()
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("Settings").AsString().Nullable()
                .WithColumn("ConfigContract").AsString().NotNullable()
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("QualityProfileId").AsInt32().NotNullable().WithDefaultValue(1)
                .WithColumn("MetadataProfileId").AsInt32().NotNullable().WithDefaultValue(1)
                .WithColumn("RootFolderPath").AsString().Nullable();
        }

        private void CreateImportWorkflowTables()
        {
            // PendingAuthorImport (string statuses + lock/lease fields)
            Create.TableForModel("PendingAuthorImport")
                .WithColumn("ProviderId").AsString(100).NotNullable()
                .WithColumn("ProviderPrefix").AsString(10).Nullable()
                .WithColumn("AuthorName").AsString().Nullable()
                .WithColumn("DiscoveredAuthorFolderPath").AsString().Nullable()
                .WithColumn("AudiobookStatus").AsString().WithDefaultValue("NotRequested")
                .WithColumn("EbookStatus").AsString().WithDefaultValue("NotRequested")
                .WithColumn("OverallStatus").AsString().WithDefaultValue("Pending")
                .WithColumn("AudiobookMonitorExisting").AsInt32().Nullable()
                .WithColumn("AudiobookMonitorFuture").AsBoolean().Nullable()
                .WithColumn("AudiobookQualityProfileId").AsInt32().Nullable()
                .WithColumn("AudiobookMetadataProfileId").AsInt32().Nullable()
                .WithColumn("AudiobookRootFolderPath").AsString().Nullable()
                .WithColumn("AudiobookBooksToMonitor").AsString().Nullable()
                .WithColumn("AudiobookBooksToSearch").AsString().Nullable()
                .WithColumn("EbookMonitorExisting").AsInt32().Nullable()
                .WithColumn("EbookMonitorFuture").AsBoolean().Nullable()
                .WithColumn("EbookQualityProfileId").AsInt32().Nullable()
                .WithColumn("EbookMetadataProfileId").AsInt32().Nullable()
                .WithColumn("EbookRootFolderPath").AsString().Nullable()
                .WithColumn("EbookBooksToMonitor").AsString().Nullable()
                .WithColumn("EbookBooksToSearch").AsString().Nullable()
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("SearchForMissingBooks").AsBoolean().WithDefaultValue(false)
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("UpdatedAt").AsDateTime().Nullable()
                .WithColumn("LastAttemptAt").AsDateTime().Nullable()
                .WithColumn("NextAttemptAt").AsDateTime().NotNullable()
                .WithColumn("AttemptCount").AsInt32().WithDefaultValue(0)
                .WithColumn("MaxAttempts").AsInt32().WithDefaultValue(0)
                .WithColumn("LastError").AsString().Nullable()
                .WithColumn("RequestedBy").AsString().Nullable()
                .WithColumn("SourceApplication").AsString().Nullable()
                .WithColumn("CorrelationId").AsString().Nullable()
                .WithColumn("LockedBy").AsString().Nullable()
                .WithColumn("LockedAt").AsDateTime().Nullable()
                .WithColumn("LeaseExpiresAt").AsDateTime().Nullable()
                .WithColumn("Version").AsInt64().NotNullable().WithDefaultValue(0);

            Create.Index("IX_PendingAuthorImport_NextAttemptAt").OnTable("PendingAuthorImport").OnColumn("NextAttemptAt");
            Create.Index("IX_PendingAuthorImport_OverallStatus").OnTable("PendingAuthorImport").OnColumn("OverallStatus");
            Create.Index("IX_PendingAuthorImport_Processing")
                .OnTable("PendingAuthorImport")
                .OnColumn("NextAttemptAt").Ascending()
                .OnColumn("OverallStatus").Ascending()
                .OnColumn("AttemptCount").Ascending();
            Create.Index("IX_PendingAuthorImport_UpdatedAt").OnTable("PendingAuthorImport").OnColumn("UpdatedAt");
            Create.Index("IX_PendingAuthorImport_ProviderId").OnTable("PendingAuthorImport").OnColumn("ProviderId");
            Create.Index("IX_PendingAuthorImport_LeaseExpiry")
                .OnTable("PendingAuthorImport")
                .OnColumn("LeaseExpiresAt").Ascending()
                .OnColumn("OverallStatus").Ascending();
            // Partial unique on active states (SQLite-only)
            IfDatabase("sqlite").Execute.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS UX_PendingAuthorImport_Active
                 ON PendingAuthorImport(ProviderId)
                 WHERE OverallStatus IN ('Pending','InProgress','Retrying')");

            // PendingImport
            Create.TableForModel("PendingImport")
                .WithColumn("ImportType").AsString().NotNullable()
                .WithColumn("ProviderIds").AsString().NotNullable()
                .WithColumn("MediaType").AsString().Nullable()
                .WithColumn("MonitoringType").AsString().Nullable()
                .WithColumn("MonitoringIds").AsString().Nullable()
                .WithColumn("Settings").AsString().Nullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("LastAttemptAt").AsDateTime().Nullable()
                .WithColumn("NextRetryAt").AsDateTime().NotNullable()
                .WithColumn("RetryCount").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("ErrorMessage").AsString().Nullable()
                .WithColumn("CompletedAt").AsDateTime().Nullable()
                .WithColumn("AuthorId").AsInt32().Nullable();
            Create.Index("IX_PendingImport_ProviderIds").OnTable("PendingImport").OnColumn("ProviderIds");
            Create.Index("IX_PendingImport_Status").OnTable("PendingImport").OnColumn("Status");
            Create.Index("IX_PendingImport_NextRetryAt").OnTable("PendingImport").OnColumn("NextRetryAt");

            // AuthorSyncMetadata
            Create.Table("AuthorSyncMetadata")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("ExternalAuthorId").AsString(128).NotNullable()
                .WithColumn("ETag").AsString(128).Nullable()
                .WithColumn("ServerVersion").AsString(128).Nullable()
                .WithColumn("LastSyncAttempt").AsDateTime().Nullable()
                .WithColumn("LastSuccessfulSync").AsDateTime().Nullable()
                .WithColumn("LastSyncStatus").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("LastHttpStatus").AsInt32().Nullable()
                .WithColumn("SyncFailureCount").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("LastError").AsString(1024).Nullable()
                .WithColumn("LastSyncDurationMs").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("NextSyncNotBefore").AsDateTime().Nullable();
            Create.Index("UX_AuthorSyncMetadata_Author").OnTable("AuthorSyncMetadata").OnColumn("AuthorId").Unique();
            Create.Index("UX_AuthorSyncMetadata_ExternalId").OnTable("AuthorSyncMetadata").OnColumn("ExternalAuthorId").Unique();
            Create.Index("IX_AuthorSyncMetadata_NextDue").OnTable("AuthorSyncMetadata").OnColumn("NextSyncNotBefore").Ascending().OnColumn("Id").Ascending();
            IfPostgres()
                .Create.ForeignKey("FK_AuthorSyncMetadata_Authors")
                .FromTable("AuthorSyncMetadata").ForeignColumn("AuthorId")
                .ToTable("Authors").PrimaryColumn("Id")
                .OnDelete(System.Data.Rule.Cascade);

            // SQLite does not support adding FKs post table creation; emulate cascade with trigger
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER IF NOT EXISTS trg_author_sync_metadata_author_delete
                AFTER DELETE ON Authors
                BEGIN
                    DELETE FROM AuthorSyncMetadata WHERE AuthorId = OLD.Id;
                END;
            ");

            // AuthorSyncQueue
            Create.TableForModel("AuthorSyncQueue")
                .WithColumn("PrefixedAuthorId").AsString().NotNullable().Unique()
                .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("AttemptCount").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("LastError").AsString().Nullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("ProcessedAt").AsDateTime().Nullable();
            Create.Index("IX_AuthorSyncQueue_Status").OnTable("AuthorSyncQueue").OnColumn("Status");
            Create.Index("IX_AuthorSyncQueue_Processing").OnTable("AuthorSyncQueue").OnColumn("Status").Ascending().OnColumn("AttemptCount").Ascending();

            // AuthorImportClaims
            // Note: TableForModel adds an auto-increment Id PK. ProviderId must be unique for ON CONFLICT to work.
            Create.TableForModel("AuthorImportClaims")
                .WithColumn("ProviderId").AsString().NotNullable()
                .WithColumn("ClaimedAt").AsInt64().NotNullable()
                .WithColumn("LeaseSec").AsInt32().NotNullable().WithDefaultValue(300);
            Create.Index("UX_AuthorImportClaims_ProviderId").OnTable("AuthorImportClaims").OnColumn("ProviderId").Unique();
            Execute.Sql("CREATE INDEX IF NOT EXISTS \"IX_AuthorImportClaims_ClaimedAt\" ON \"AuthorImportClaims\"(\"ClaimedAt\");");

            // Path LIKE optimization for SQLite
            IfDatabase("sqlite").Execute.Sql("CREATE INDEX IF NOT EXISTS \"IX_BookFiles_Path_Binary\" ON \"BookFiles\" (\"Path\" COLLATE BINARY);");
        }

        private void CreateQualityTables()
        {
            // Quality definitions
            Create.TableForModel("QualityDefinitions")
                .WithColumn("Quality").AsInt32().NotNullable().Unique()
                .WithColumn("Title").AsString().NotNullable().Unique()
                .WithColumn("MinSize").AsDouble().Nullable()
                .WithColumn("MaxSize").AsDouble().Nullable()
                .WithColumn("PreferredSize").AsDouble().Nullable();

            // Search criteria profiles (Chaptarr specific)
            Create.TableForModel("SearchCriteriaProfiles")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Items").AsString().NotNullable()
                .WithColumn("IsDefault").AsBoolean().NotNullable();

            // Quality profiles
            Create.TableForModel("QualityProfiles")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("ProfileType").AsInt32().NotNullable()
                .WithColumn("Cutoff").AsInt32().NotNullable()
                .WithColumn("Items").AsString().NotNullable()
                .WithColumn("Language").AsInt32().Nullable()
                .WithColumn("UpgradeAllowed").AsBoolean().NotNullable()
                .WithColumn("MinFormatScore").AsInt32().NotNullable()
                .WithColumn("CutoffFormatScore").AsInt32().NotNullable()
                .WithColumn("FormatItems").AsString().NotNullable()
                .WithColumn("SearchCriteriaProfileId").AsInt32().Nullable()
                .WithColumn("ConvertMp3ToM4b").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("ConvertToQualityId").AsInt32().Nullable();

            // Metadata profiles
            Create.TableForModel("MetadataProfiles")
                .WithColumn("Name").AsString().NotNullable().Unique()
                .WithColumn("ProfileType").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("MinPopularity").AsDouble().NotNullable().WithDefaultValue(0.0)
                .WithColumn("SkipMissingDate").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipMissingIsbn").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipPartsAndSets").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipSeriesSecondary").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipMissingIdentifierOmnibus").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipOmnibus").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("SkipMissingAsin").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("AllowedLanguages").AsString().Nullable()
                .WithColumn("MinPages").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Ignored").AsString().NotNullable().WithDefaultValue("[]")
                // Legacy columns that might still be needed
                .WithColumn("PrimaryAlbumTypes").AsString().NotNullable().WithDefaultValue("[]")
                .WithColumn("SecondaryAlbumTypes").AsString().NotNullable().WithDefaultValue("[]")
                .WithColumn("ReleaseStatuses").AsString().NotNullable().WithDefaultValue("[]")
                .WithColumn("PreferredTags").AsString().NotNullable().WithDefaultValue("[]")
                .WithColumn("IgnoredTags").AsString().NotNullable().WithDefaultValue("[]");

            // Custom formats
            Create.TableForModel("CustomFormats")
                .WithColumn("Name").AsString().NotNullable().Unique()
                .WithColumn("Specifications").AsString().NotNullable()
                .WithColumn("IncludeCustomFormatWhenRenaming").AsBoolean().NotNullable();
        }

        private void CreateIndexerTables()
        {
            // Indexers
            Create.TableForModel("Indexers")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("Settings").AsString().Nullable()
                .WithColumn("ConfigContract").AsString().NotNullable()
                .WithColumn("Enable").AsBoolean().Nullable()
                .WithColumn("SupportsRss").AsBoolean().NotNullable()
                .WithColumn("SupportsSearch").AsBoolean().NotNullable()
                .WithColumn("Priority").AsInt32().NotNullable()
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("DownloadClientId").AsInt32().WithDefaultValue(0)
                // Added for baseline parity
                .WithColumn("EnableRss").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("EnableAutomaticSearch").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("EnableInteractiveSearch").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("ProxyId").AsInt32().Nullable();

            // Indexer status
            Create.TableForModel("IndexerStatus")
                .WithColumn("ProviderId").AsInt32().NotNullable().Unique()
                .WithColumn("InitialFailure").AsDateTime().Nullable()
                .WithColumn("MostRecentFailure").AsDateTime().Nullable()
                .WithColumn("EscalationLevel").AsInt32().NotNullable()
                .WithColumn("DisabledTill").AsDateTime().Nullable()
                .WithColumn("LastRssSyncReleaseInfo").AsString().Nullable()
                .WithColumn("Cookies").AsString().Nullable()
                .WithColumn("CookiesExpirationDate").AsDateTime().Nullable();
        }

        private void CreateNotificationTables()
        {
            // Notifications
            Create.TableForModel("Notifications")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("OnGrab").AsBoolean().NotNullable()
                .WithColumn("Settings").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("ConfigContract").AsString().Nullable()
                .WithColumn("OnUpgrade").AsBoolean().Nullable()
                .WithColumn("Tags").AsString().Nullable()
                .WithColumn("OnRename").AsBoolean().NotNullable()
                .WithColumn("OnAuthorAdded").AsBoolean().NotNullable()
                .WithColumn("OnBookAdded").AsBoolean().NotNullable()
                .WithColumn("OnAuthorDelete").AsBoolean().NotNullable()
                .WithColumn("OnBookDelete").AsBoolean().NotNullable()
                .WithColumn("OnBookFileDelete").AsBoolean().NotNullable()
                .WithColumn("OnBookFileDeleteForUpgrade").AsBoolean().NotNullable()
                .WithColumn("OnHealthIssue").AsBoolean().NotNullable()
                .WithColumn("IncludeHealthWarnings").AsBoolean().NotNullable()
                .WithColumn("OnHealthRestored").AsBoolean().NotNullable()
                .WithColumn("OnDownloadFailure").AsBoolean().NotNullable()
                .WithColumn("OnImportFailure").AsBoolean().NotNullable()
                .WithColumn("OnBookRetag").AsBoolean().NotNullable()
                .WithColumn("OnApplicationUpdate").AsBoolean().NotNullable()
                .WithColumn("OnReleaseImport").AsBoolean().NotNullable()
                // Capability flags (default true)
                .WithColumn("SupportsOnGrab").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnDownload").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnUpgrade").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnHealthIssue").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnApplicationUpdate").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnManualInteraction").AsBoolean().NotNullable().WithDefaultValue(true)
                // Folded from 003_add_notification_supports_columns to avoid incremental migrations
                .WithColumn("SupportsOnReleaseImport").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnRename").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnAuthorAdded").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnBookAdded").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnAuthorDelete").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnBookDelete").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnBookFileDelete").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnBookFileDeleteForUpgrade").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnHealthRestored").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnDownloadFailure").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnImportFailure").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("SupportsOnBookRetag").AsBoolean().NotNullable().WithDefaultValue(true);

            // Metadata
            Create.TableForModel("Metadata")
                .WithColumn("Enable").AsBoolean().NotNullable()
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("Settings").AsString().NotNullable()
                .WithColumn("ConfigContract").AsString().NotNullable()
                .WithColumn("Tags").AsString().Nullable();
        }

        private void CreateNotificationStatusTables()
        {
            // Notification status tracking
            Create.TableForModel("NotificationStatus")
                .WithColumn("ProviderId").AsInt32().NotNullable().Unique()
                .WithColumn("InitialFailure").AsDateTime().Nullable()
                .WithColumn("MostRecentFailure").AsDateTime().Nullable()
                .WithColumn("EscalationLevel").AsInt32().NotNullable()
                .WithColumn("DisabledTill").AsDateTime().Nullable();
        }

        private void CreateApplicationTables()
        {
            // Applications
            Create.TableForModel("Applications")
                .WithColumn("Name").AsString().NotNullable()
                .WithColumn("Implementation").AsString().NotNullable()
                .WithColumn("Settings").AsString().Nullable()
                .WithColumn("ConfigContract").AsString().NotNullable()
                .WithColumn("SyncLevel").AsInt32().NotNullable()
                .WithColumn("Tags").AsString().Nullable();

            // Application status
            Create.TableForModel("ApplicationStatus")
                .WithColumn("ProviderId").AsInt32().NotNullable().Unique()
                .WithColumn("InitialFailure").AsDateTime().Nullable()
                .WithColumn("MostRecentFailure").AsDateTime().Nullable()
                .WithColumn("EscalationLevel").AsInt32().NotNullable()
                .WithColumn("DisabledTill").AsDateTime().Nullable();

            // Download client status
            Create.TableForModel("DownloadClientStatus")
                .WithColumn("ProviderId").AsInt32().NotNullable().Unique()
                .WithColumn("InitialFailure").AsDateTime().Nullable()
                .WithColumn("MostRecentFailure").AsDateTime().Nullable()
                .WithColumn("EscalationLevel").AsInt32().NotNullable()
                .WithColumn("DisabledTill").AsDateTime().Nullable();
        }

        private void CreateIntegrationTables()
        {
            // Chaptarr: AudioBookShelf integration
            Create.TableForModel("ABSLookupHistory")
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("RequestType").AsString().Nullable()
                .WithColumn("LastChecked").AsDateTime().NotNullable()
                .WithColumn("Confidence").AsString().Nullable()
                .WithColumn("FoundNarrator").AsString().Nullable()
                .WithColumn("ABSResponse").AsString().Nullable()
                .WithColumn("InstanceId").AsString().Nullable()
                .WithColumn("DurationMinutes").AsDouble().Nullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable();

            Create.TableForModel("ABSRateLimit")
                .WithColumn("InstanceId").AsString().NotNullable()
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("AutoRequestCount").AsInt32().NotNullable()
                .WithColumn("ManualRequestCount").AsInt32().NotNullable()
                .WithColumn("LastRequestTime").AsDateTime().Nullable();
        }

        private void CreateUpdateHistoryTables()
        {
            // Update history for tracking application updates
            Create.TableForModel("UpdateHistory")
                .WithColumn("Date").AsDateTime().NotNullable()
                .WithColumn("Version").AsString().NotNullable()
                .WithColumn("EventType").AsInt32().NotNullable();
        }

        private void CreateFuzzyMatchingTables()
        {
            // Book trigrams for fuzzy title matching
            Create.Table("book_trigrams")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("BookId").AsInt32().NotNullable()
                .WithColumn("Trigram").AsString().NotNullable();

            // Author trigrams for fuzzy name matching
            Create.Table("author_trigrams")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("AuthorId").AsInt32().NotNullable()
                .WithColumn("Trigram").AsString().NotNullable();

            // Normalized book titles for exact matching after normalization
            Create.Table("book_normalized")
                .WithColumn("BookId").AsInt32().PrimaryKey()
                .WithColumn("NormalizedTitle").AsString().NotNullable();

            // Normalized author names for exact matching after normalization
            Create.Table("author_normalized")
                .WithColumn("AuthorId").AsInt32().PrimaryKey()
                .WithColumn("NormalizedName").AsString().NotNullable();

            // Normalized narrator names for audiobook edition matching
            Create.Table("narrator_normalized")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("NarratorName").AsString().NotNullable()
                .WithColumn("NormalizedName").AsString().NotNullable()
                .WithColumn("EditionId").AsInt32().Nullable();
        }

        private void CreateFtsSearchTables()
        {
            // SQLite FTS5 implementation (final tokenizer)
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE IF NOT EXISTS edition_fts USING fts5(" +
                "Title, TitleSlug, " +
                "content='Editions', content_rowid='Id', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, '')); " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE IF NOT EXISTS author_fts USING fts5(" +
                "Name, SortName, TitleSlug, " +
                "content='Authors', content_rowid='Id', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS author_fts_ai AFTER INSERT ON Authors BEGIN " +
                "INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS author_fts_ad AFTER DELETE ON Authors BEGIN " +
                "INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER IF NOT EXISTS author_fts_au AFTER UPDATE ON Authors BEGIN " +
                "INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, '')); " +
                "INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            // PostgreSQL full-text search using GIN on generated vectors
            IfPostgres().Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_authors_fts
                ON ""Authors""
                USING GIN (
                    to_tsvector('simple', COALESCE(""Name"", '') || ' ' || COALESCE(""CleanName"", '') || ' ' || COALESCE(""TitleSlug"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_editions_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple', COALESCE(""Title"", '') || ' ' || COALESCE(""TitleSlug"", ''))
                );
            ");
        }

        private void CreateIndexes()
        {
            // Performance indexes for Chaptarr
            Create.Index("IX_Books_AuthorId").OnTable("Books").OnColumn("AuthorId");
            Create.Index("IX_Books_AuthorId_Id").OnTable("Books").OnColumn("AuthorId").Ascending().OnColumn("Id").Ascending();
            Create.Index("IX_Books_CleanTitle").OnTable("Books").OnColumn("CleanTitle");
            // Removed: Books does not have a single Monitored column
            Create.Index("IX_Books_NarratorId").OnTable("Books").OnColumn("NarratorId");
            Create.Index("IX_Books_MediaType").OnTable("Books").OnColumn("MediaType");
            Create.Index("IX_Books_AuthorId_Title").OnTable("Books")
                .OnColumn("AuthorId").Ascending()
                .OnColumn("Title").Ascending();

            Create.Index("IX_Editions_BookId").OnTable("Editions").OnColumn("BookId");
            Create.Index("IX_Editions_Monitored").OnTable("Editions").OnColumn("Monitored");
            Create.Index("IX_BookFiles_EditionId").OnTable("BookFiles").OnColumn("EditionId");
            Create.Index("IX_MetadataFiles_BookFileId").OnTable("MetadataFiles").OnColumn("BookFileId");
            Create.Index("IX_History_BookId").OnTable("History").OnColumn("BookId");
            Create.Index("IX_History_AuthorId").OnTable("History").OnColumn("AuthorId");
            Create.Index("IX_History_EditionId").OnTable("History").OnColumn("EditionId");
            Create.Index("IX_History_Date").OnTable("History").OnColumn("Date");
            Create.Index("IX_DownloadClientFileSnapshots_Client_Download")
                .OnTable("DownloadClientFileSnapshots")
                .OnColumn("DownloadClientId").Ascending()
                .OnColumn("DownloadId").Ascending()
                .WithOptions().Unique();
            Create.Index("IX_DownloadClientFileSnapshots_LastUpdated")
                .OnTable("DownloadClientFileSnapshots")
                .OnColumn("LastUpdated").Ascending();
            Create.Index("IX_NarratorMetadata_Name").OnTable("NarratorMetadata").OnColumn("Name");

            Create.Index("IX_Series_Title_Narrator").OnTable("Series")
                .OnColumn("Title").Ascending()
                .OnColumn("Narrator").Ascending();
            Create.Index("IX_Series_BaseSeriesId").OnTable("Series").OnColumn("BaseSeriesId");
            Create.Index("IX_Series_InstanceType").OnTable("Series").OnColumn("InstanceType");

            // Unique constraints for narrator relationships
            Create.Index("IX_BookNarratorLink_BookId_NarratorId").OnTable("BookNarratorLink")
                .OnColumn("BookId").Ascending()
                .OnColumn("NarratorId").Ascending()
                .WithOptions().Unique();

            Create.Index("IX_EditionNarratorLink_EditionId_NarratorId").OnTable("EditionNarratorLink")
                .OnColumn("EditionId").Ascending()
                .OnColumn("NarratorId").Ascending()
                .WithOptions().Unique();

            Create.Index("IX_SeriesBookLink_BookId_SeriesId_InstanceType").OnTable("SeriesBookLink")
                .OnColumn("BookId").Ascending()
                .OnColumn("SeriesId").Ascending()
                .OnColumn("SeriesInstanceType").Ascending()
                .WithOptions().Unique();

            Create.Index("IX_SeriesBookLink_SeriesId").OnTable("SeriesBookLink")
                .OnColumn("SeriesId").Ascending();

            // Fuzzy matching indexes
            Create.Index("IX_book_trigrams_BookId").OnTable("book_trigrams").OnColumn("BookId");
            Create.Index("IX_book_trigrams_Trigram").OnTable("book_trigrams").OnColumn("Trigram");
            Create.Index("IX_author_trigrams_AuthorId").OnTable("author_trigrams").OnColumn("AuthorId");
            Create.Index("IX_author_trigrams_Trigram").OnTable("author_trigrams").OnColumn("Trigram");
            Create.Index("IX_book_normalized_NormalizedTitle").OnTable("book_normalized").OnColumn("NormalizedTitle");
            Create.Index("IX_author_normalized_NormalizedName").OnTable("author_normalized").OnColumn("NormalizedName");
            Create.Index("IX_narrator_normalized_NormalizedName").OnTable("narrator_normalized").OnColumn("NormalizedName");
            Create.Index("IX_narrator_normalized_EditionId").OnTable("narrator_normalized").OnColumn("EditionId");

            // Provider ID indexes
            Create.Index("IX_Authors_HardcoverAuthorId").OnTable("Authors").OnColumn("HardcoverAuthorId");
            Create.Index("IX_Books_HardcoverBookId").OnTable("Books").OnColumn("HardcoverBookId");
            Create.Index("IX_Series_HardcoverSeriesId").OnTable("Series").OnColumn("HardcoverSeriesId");

            // Partial indexes for performance
            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_BookFiles_Path_Partial
                    ON BookFiles(Path)
                    WHERE Path IS NOT NULL");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_Asin_Partial
                    ON Editions(Asin)
                    WHERE Asin IS NOT NULL AND LENGTH(Asin) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_AudibleASIN_Partial
                    ON Editions(AudibleASIN)
                    WHERE AudibleASIN IS NOT NULL AND LENGTH(AudibleASIN) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_Isbn10_Partial
                    ON Editions(Isbn10)
                    WHERE Isbn10 IS NOT NULL AND LENGTH(Isbn10) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_Isbn13_Partial
                    ON Editions(Isbn13)
                    WHERE Isbn13 IS NOT NULL AND LENGTH(Isbn13) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_NarratorNames_Perf
                    ON Editions(NarratorNames)
                    WHERE LENGTH(NarratorNames) > 2 AND NarratorNames != '[]'");

            // PostgreSQL functional indexes for case-insensitive ID matches
            IfPostgres()
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_Asin_Upper
                    ON ""Editions"" (UPPER(""Asin""))
                    WHERE ""Asin"" IS NOT NULL AND LENGTH(""Asin"") > 0");

            IfPostgres()
                .Execute.Sql(@"
                    CREATE INDEX IX_Editions_AudibleASIN_Upper
                    ON ""Editions"" (UPPER(""AudibleASIN""))
                    WHERE ""AudibleASIN"" IS NOT NULL AND LENGTH(""AudibleASIN"") > 0");
        }

        private void CreateConstraints()
        {
            // Foreign key constraint - Books MUST have valid Authors (Postgres only; SQLite inline FKs not added here)
            IfPostgres()
                .Create.ForeignKey("FK_Books_Authors")
                .FromTable("Books").ForeignColumn("AuthorId")
                .ToTable("Authors").PrimaryColumn("Id");
        }

        private void InsertDefaultData()
        {
            // Default delay profile
            Insert.IntoTable("DelayProfiles").Row(new
            {
                EnableUsenet = true,
                EnableTorrent = true,
                PreferredProtocol = 1,
                UsenetDelay = 0,
                TorrentDelay = 0,
                Order = int.MaxValue,
                Tags = "[]",
                BypassIfHighestQuality = false,
                BypassIfAboveCustomFormatScore = false,
                MinimumCustomFormatScore = (int?)null
            });

            // Default search criteria profile
            Insert.IntoTable("SearchCriteriaProfiles").Row(new
            {
                Name = "Standard",
                Items = @"[
                    {
                        ""type"": 1,
                        ""enabled"": true,
                        ""settings"": {""preferredNarrators"": []}
                    },
                    {
                        ""type"": 2,
                        ""enabled"": true,
                        ""settings"": {""tolerancePercentage"": 10}
                    },
                    {
                        ""type"": 3,
                        ""enabled"": true,
                        ""settings"": {}
                    }
                ]",
                IsDefault = true
            });

            // E-Book quality profile
            Insert.IntoTable("QualityProfiles").Row(new
            {
                Name = "E-Book",
                ProfileType = 2, // Ebook
                Cutoff = 4, // AZW3
                Items = @"[
                    {""quality"": 0, ""allowed"": true},
                    {""quality"": 1, ""allowed"": true},
                    {""quality"": 2, ""allowed"": true},
                    {""quality"": 3, ""allowed"": true},
                    {""quality"": 4, ""allowed"": true}
                ]",
                Language = (int?)null,
                UpgradeAllowed = false,
                MinFormatScore = 0,
                CutoffFormatScore = 0,
                FormatItems = "[]",
                SearchCriteriaProfileId = 1
            });

            // Audiobook quality profile
            Insert.IntoTable("QualityProfiles").Row(new
            {
                Name = "Audiobook",
                ProfileType = 1, // Audiobook
                Cutoff = 12, // M4B cutoff
                // Storage order: worst -> best (display will reverse to show best first)
                Items = @"[
                    {""quality"": 13, ""allowed"": true},
                    {""quality"": 11, ""allowed"": true},
                    {""quality"": 10, ""allowed"": true},
                    {""quality"": 12, ""allowed"": true}
                ]",
                Language = (int?)null,
                UpgradeAllowed = false,
                MinFormatScore = 0,
                CutoffFormatScore = 0,
                FormatItems = "[]",
                SearchCriteriaProfileId = 1
            });

            // Default metadata profiles - type-specific
            Insert.IntoTable("MetadataProfiles").Row(new
            {
                Name = "Audiobook Default",
                ProfileType = 1, // Audiobook
                MinPopularity = 0.0,
                SkipMissingDate = false,
                SkipMissingIsbn = false,
                SkipPartsAndSets = false,
                SkipSeriesSecondary = false,
                AllowedLanguages = (string)null,
                MinPages = 0,
                Ignored = "[]",
                PrimaryAlbumTypes = "[]",
                SecondaryAlbumTypes = "[]",
                ReleaseStatuses = "[]",
                PreferredTags = "[]",
                IgnoredTags = "[]"
            });

            Insert.IntoTable("MetadataProfiles").Row(new
            {
                Name = "Ebook Default",
                ProfileType = 2, // Ebook
                MinPopularity = 0.0,
                SkipMissingDate = false,
                SkipMissingIsbn = false,
                SkipPartsAndSets = false,
                SkipSeriesSecondary = false,
                AllowedLanguages = (string)null,
                MinPages = 0,
                Ignored = "[]",
                PrimaryAlbumTypes = "[]",
                SecondaryAlbumTypes = "[]",
                ReleaseStatuses = "[]",
                PreferredTags = "[]",
                IgnoredTags = "[]"
            });

            // Quality definitions
            var qualityDefinitions = new object[]
            {
                // Text formats
                new { Quality = 0, Title = "Unknown Text", MinSize = (double?)null, MaxSize = (double?)null },
                new { Quality = 1, Title = "PDF", MinSize = (double?)null, MaxSize = 350.0 },
                new { Quality = 2, Title = "MOBI", MinSize = (double?)null, MaxSize = 350.0 },
                new { Quality = 3, Title = "EPUB", MinSize = (double?)null, MaxSize = 350.0 },
                new { Quality = 4, Title = "AZW3", MinSize = (double?)null, MaxSize = 350.0 },

                // Audio formats (simplified)
                new { Quality = 10, Title = "MP3", MinSize = (double?)null, MaxSize = 350.0 },
                new { Quality = 11, Title = "FLAC", MinSize = (double?)null, MaxSize = (double?)null },
                new { Quality = 12, Title = "M4B", MinSize = (double?)null, MaxSize = 350.0 },
                new { Quality = 13, Title = "Unknown Audio", MinSize = (double?)null, MaxSize = 350.0 }
            };

            foreach (var definition in qualityDefinitions)
            {
                Insert.IntoTable("QualityDefinitions").Row(definition);
            }

            // Default naming config
            Insert.IntoTable("NamingConfig").Row(new
            {
                MultiEpisodeStyle = 0,
                ReplaceIllegalCharacters = true,
                // Default path under author: /{AuthorFolder}/{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:000)}
                // AuthorFolderFormat controls author folder naming; StandardBookFormat controls subpath + filename
                StandardBookFormat = "{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:000)}",
                AuthorFolderFormat = "{Author Name}",
                RenameBooks = false
            });
        }
    }
}
