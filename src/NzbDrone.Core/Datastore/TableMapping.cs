using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Common.Reflection;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFilters;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.History;
using NzbDrone.Core.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Update.History;
using static Dapper.SqlMapper;

namespace NzbDrone.Core.Datastore
{
    public static class TableMapping
    {
        static TableMapping()
        {
            Mapper = new TableMapper();
        }

        public static TableMapper Mapper { get; private set; }

        public static void Map()
        {
            RegisterMappers();

            Mapper.Entity<Config>("Config").RegisterModel();

            Mapper.Entity<RootFolder>("RootFolders").RegisterModel()
                  .Ignore(r => r.Accessible)
                  .Ignore(r => r.FreeSpace)
                  .Ignore(r => r.TotalSpace);

            Mapper.Entity<ScheduledTask>("ScheduledTasks").RegisterModel()
                  .Ignore(i => i.Priority);

            Mapper.Entity<IndexerDefinition>("Indexers").RegisterModel()
                  .Ignore(x => x.ImplementationName)
                  .Ignore(i => i.Enable)
                  .Ignore(i => i.Protocol);

            Mapper.Entity<ImportListDefinition>("ImportLists").RegisterModel()
                  .Ignore(x => x.ImplementationName)
                  .Ignore(i => i.ListType)
                  .Ignore(i => i.MinRefreshInterval)
                  .Ignore(i => i.Enable);

            Mapper.Entity<NotificationDefinition>("Notifications").RegisterModel()
                  .Ignore(x => x.ImplementationName)
                  .Ignore(i => i.SupportsOnGrab)
                  .Ignore(i => i.SupportsOnReleaseImport)
                  .Ignore(i => i.SupportsOnUpgrade)
                  .Ignore(i => i.SupportsOnRename)
                  .Ignore(i => i.SupportsOnAuthorAdded)
                  .Ignore(i => i.SupportsOnAuthorDelete)
                  .Ignore(i => i.SupportsOnBookDelete)
                  .Ignore(i => i.SupportsOnBookFileDelete)
                  .Ignore(i => i.SupportsOnBookFileDeleteForUpgrade)
                  .Ignore(i => i.SupportsOnHealthIssue)
                  .Ignore(i => i.SupportsOnDownloadFailure)
                  .Ignore(i => i.SupportsOnImportFailure)
                  .Ignore(i => i.SupportsOnBookRetag)
                  .Ignore(i => i.SupportsOnApplicationUpdate);

            Mapper.Entity<MetadataDefinition>("Metadata").RegisterModel()
                  .Ignore(x => x.ImplementationName)
                  .Ignore(d => d.Tags);

            Mapper.Entity<DownloadClientDefinition>("DownloadClients").RegisterModel()
                  .Ignore(x => x.ImplementationName)
                  .Ignore(d => d.Protocol);

            Mapper.Entity<EntityHistory>("History").RegisterModel();

            Mapper.Entity<Author>("Authors").RegisterModel()
                  .Ignore(s => s.Books)
                  .Ignore(s => s.Series)
                  .Ignore(s => s.RemoteProviderIds)
                  .Ignore(s => s.RemoteMetadataETag)
                  .HasOne(a => a.AudiobookQualityProfile, a => a.AudiobookQualityProfileId ?? 0)
                  .HasOne(a => a.EbookQualityProfile, a => a.EbookQualityProfileId ?? 0)
                  .HasOne(a => a.MetadataProfile, a => a.MetadataProfileId ?? 0)
                  .HasOne(a => a.AudiobookMetadataProfile, a => a.AudiobookMetadataProfileId ?? 0)
                  .HasOne(a => a.EbookMetadataProfile, a => a.EbookMetadataProfileId ?? 0);

            Mapper.Entity<AuthorSyncMetadata>("AuthorSyncMetadata").RegisterModel();
            
            Mapper.Entity<AuthorSyncQueue>("AuthorSyncQueue").RegisterModel();
            Mapper.Entity<ProviderAlias>("ProviderAliasIndex").RegisterModel();

            Mapper.Entity<PendingAuthorImport>("PendingAuthorImport").RegisterModel();
            Mapper.Entity<ImportListBookIdentityCache>("ImportListBookIdentityCache").RegisterModel();
            Mapper.Entity<PendingImport>("PendingImport").RegisterModel();

            Mapper.Entity<Series>("Series").RegisterModel()
                  .Ignore(s => s.Books)
                  .Ignore(s => s.SeriesBooks)
                  .Ignore(s => s.LinkItems)
                  .LazyLoad(s => s.LazyLinkItems,
                            (db, series) => db.Query<SeriesBookLink>(new SqlBuilder(db.DatabaseType)
                                                                      .Where<SeriesBookLink>(link => link.SeriesId == series.Id)).ToList(),
                            s => s.Id > 0)
                  .LazyLoad(s => s.LazyBooks,
                            (db, series) => db.Query<Book>(new SqlBuilder(db.DatabaseType)
                                                           .Join<Book, SeriesBookLink>((book, link) => book.Id == link.BookId)
                                                           .Where<SeriesBookLink>(link => link.SeriesId == series.Id)).ToList(),
                            s => s.Id > 0);

            Mapper.Entity<SeriesBookLink>("SeriesBookLink").RegisterModel()
                  .HasOne(l => l.Book, l => l.BookId)
                  .HasOne(l => l.Series, l => l.SeriesId);

            // Chaptarr: Narrator models
            Mapper.Entity<NarratorMetadata>("NarratorMetadata").RegisterModel();
            Mapper.Entity<Narrator>("Narrators").RegisterModel()
                  .Ignore(n => n.Name)
                  .HasOne(n => n.Metadata, n => n.NarratorMetadataId);

            Mapper.Entity<BookNarratorOption>("BookNarratorOptions").RegisterModel()
                  .HasOne(o => o.Book, o => o.BookId);

            Mapper.Entity<BookNarratorLink>("BookNarratorLink").RegisterModel()
                  .HasOne(l => l.Book, l => l.BookId)
                  .HasOne(l => l.Narrator, l => l.NarratorId);

            Mapper.Entity<EditionNarratorLink>("EditionNarratorLink").RegisterModel()
                  .HasOne(l => l.Edition, l => l.EditionId)
                  .HasOne(l => l.Narrator, l => l.NarratorId);

            Mapper.Entity<Book>("Books").RegisterModel()
                .Ignore(x => x.ForeignEditionId)
                .Ignore(x => x.NarratorName)
                .Ignore(x => x.RemoteProviderIds)
                // Legacy field removed from schema; use AudiobookMonitored/EbookMonitored
                .Ignore(x => x.Monitored)
                .LazyLoad(x => x.LazyBookFiles,
                          (db, book) => db.Query<BookFile>(new SqlBuilder(db.DatabaseType)
                                                           .Join<BookFile, Edition>((file, edition) => file.EditionId == edition.Id)
                                                           .Where<Edition>(edition => edition.BookId == book.Id)).ToList(),
                          b => b.Id > 0)
                .LazyLoad(x => x.LazyEditions,
                          (db, book) => db.Query<Edition>(new SqlBuilder(db.DatabaseType)
                                                          .Where<Edition>(edition => edition.BookId == book.Id)).ToList(),
                          b => b.Id > 0)
                .LazyLoad(x => x.LazyAuthor,
                          (db, book) => AuthorRepository.Query(db,
                                                               new SqlBuilder(db.DatabaseType)
                                                                   .Join<Author, Book>((author, innerBook) => author.Id == innerBook.AuthorId)
                                                                   .Where<Book>(innerBook => innerBook.Id == book.Id)).SingleOrDefault(),
                          b => b.Id > 0)
                .LazyLoad(x => x.LazySeriesLinks,
                          (db, book) => db.Query<SeriesBookLink>(new SqlBuilder(db.DatabaseType)
                                                                 .Where<SeriesBookLink>(link => link.BookId == book.Id)).ToList(),
                          b => b.Id > 0);

            Mapper.Entity<Edition>("Editions").RegisterModel()
                .Ignore(e => e.NarratorCredits)
                .HasOne(e => e.LazyBook, e => e.BookId);

            Mapper.Entity<BookFile>("BookFiles").RegisterModel()
                .Ignore(x => x.PartCount)
                .HasOne(f => f.LazyEdition, f => f.EditionId)
                .LazyLoad(f => f.LazyAuthor,
                          (db, file) => AuthorRepository.Query(db,
                                                                new SqlBuilder(db.DatabaseType)
                                                                    .Join<Author, Book>((author, book) => author.Id == book.AuthorId)
                                                                    .Join<Book, Edition>((book, edition) => book.Id == edition.BookId)
                                                                    .Where<Edition>(edition => edition.Id == file.EditionId)).SingleOrDefault(),
                          f => f.EditionId > 0);

            Mapper.Entity<QualityDefinition>("QualityDefinitions").RegisterModel()
                  .Ignore(d => d.GroupName)
                  .Ignore(d => d.GroupWeight)
                  .Ignore(d => d.Weight);

            Mapper.Entity<CustomFormat>("CustomFormats").RegisterModel();

            Mapper.Entity<QualityProfile>("QualityProfiles").RegisterModel();
            Mapper.Entity<MetadataProfile>("MetadataProfiles").RegisterModel();
            Mapper.Entity<Log>("Logs").RegisterModel();
            Mapper.Entity<NamingConfig>("NamingConfig").RegisterModel();

            Mapper.Entity<Blocklist>("Blocklist").RegisterModel();
            Mapper.Entity<MetadataFile>("MetadataFiles").RegisterModel();
            Mapper.Entity<OtherExtraFile>("ExtraFiles").RegisterModel();

            Mapper.Entity<PendingRelease>("PendingReleases").RegisterModel()
                  .Ignore(e => e.RemoteBook);

            Mapper.Entity<RemotePathMapping>("RemotePathMappings").RegisterModel();
            Mapper.Entity<Tag>("Tags").RegisterModel();
            Mapper.Entity<ReleaseProfile>("ReleaseProfiles").RegisterModel();

            Mapper.Entity<DelayProfile>("DelayProfiles").RegisterModel();
            Mapper.Entity<User>("Users").RegisterModel();
            Mapper.Entity<CommandModel>("Commands").RegisterModel()
                  .Ignore(c => c.Message);

            Mapper.Entity<IndexerStatus>("IndexerStatus").RegisterModel();
            Mapper.Entity<DownloadClientStatus>("DownloadClientStatus").RegisterModel();
            Mapper.Entity<ImportListStatus>("ImportListStatus").RegisterModel();
            Mapper.Entity<NotificationStatus>("NotificationStatus").RegisterModel();

            Mapper.Entity<CustomFilter>("CustomFilters").RegisterModel();
            Mapper.Entity<ImportListExclusion>("ImportListExclusions").RegisterModel();

            Mapper.Entity<CachedHttpResponse>("HttpResponse").RegisterModel();

            Mapper.Entity<DownloadHistory>("DownloadHistory").RegisterModel();

            Mapper.Entity<DownloadClientFileSnapshot>("DownloadClientFileSnapshots").RegisterModel();
            Mapper.Entity<MamUnsatisfiedSlotReservation>("MamUnsatisfiedSlotReservations").RegisterModel();
            Mapper.Entity<ConversionJob>("ConversionJobs").RegisterModel();

            Mapper.Entity<HardcoverLibraryImportListState>("HardcoverLibraryImportListState").RegisterModel();

            Mapper.Entity<UpdateHistory>("UpdateHistory").RegisterModel();

            // Proxy management
            Mapper.Entity<ProxyDefinition>("Proxies").RegisterModel();

            // Chaptarr: Local ID counter system (removed - using database IDs directly)
        }

        private static void RegisterMappers()
        {
            RegisterEmbeddedConverter();
            RegisterProviderSettingConverter();

            SqlMapper.RemoveTypeMap(typeof(DateTime));
            SqlMapper.AddTypeHandler(new DapperUtcConverter());
            SqlMapper.AddTypeHandler(new DapperTimeSpanConverter());
            SqlMapper.AddTypeHandler(new DapperQualityIntConverter());
            SqlMapper.AddTypeHandler(new ProviderUrlMapConverter());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<QualityProfileQualityItem>>(new QualityIntConverter()));
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<ProfileFormatItem>>(new CustomFormatIntConverter()));
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<ICustomFormatSpecification>>(new CustomFormatSpecificationListConverter()));
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<QualityModel>(new QualityIntConverter()));
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<Dictionary<string, string>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<IDictionary<string, string>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<Dictionary<string, object>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<int>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<KeyValuePair<string, int>>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<KeyValuePair<string, int>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<List<string>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<Dictionary<string, List<string>>>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<ParsedBookInfo>());
            // Removed ParsedTrackInfo embedded converter (field-agnostic import)
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<ReleaseInfo>());
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<HashSet<int>>());
            SqlMapper.AddTypeHandler(new OsPathConverter());
            SqlMapper.RemoveTypeMap(typeof(Guid));
            SqlMapper.RemoveTypeMap(typeof(Guid?));
            SqlMapper.AddTypeHandler(new GuidConverter());
            SqlMapper.AddTypeHandler(new CommandConverter());
            SqlMapper.AddTypeHandler(new SystemVersionConverter());
            SqlMapper.AddTypeHandler(new FolderTypeIntConverter());
        }

        private static void RegisterProviderSettingConverter()
        {
            var settingTypes = typeof(IProviderConfig).Assembly.ImplementationsOf<IProviderConfig>()
                .Where(x => !x.ContainsGenericParameters && !x.IsInterface && !x.IsAbstract);

            var providerSettingConverter = new ProviderSettingConverter();

            // ProviderDefinition.Settings is typed as IProviderConfig, so we must register the handler for the
            // interface itself (not only concrete config types) or Dapper will treat Settings as non-mappable
            // and omit it from INSERT/UPDATE statements (leaving Settings NULL in the database).
            SqlMapper.AddTypeHandler(typeof(IProviderConfig), providerSettingConverter);

            foreach (var embeddedType in settingTypes)
            {
                SqlMapper.AddTypeHandler(embeddedType, providerSettingConverter);
            }
        }

        private static void RegisterEmbeddedConverter()
        {
            var embeddedTypes = typeof(IEmbeddedDocument).Assembly.ImplementationsOf<IEmbeddedDocument>();

            var embeddedConverterDefinition = typeof(EmbeddedDocumentConverter<>).GetGenericTypeDefinition();
            var genericListDefinition = typeof(List<>).GetGenericTypeDefinition();

            foreach (var embeddedType in embeddedTypes)
            {
                var embeddedListType = genericListDefinition.MakeGenericType(embeddedType);

                RegisterEmbeddedConverter(embeddedType, embeddedConverterDefinition);
                RegisterEmbeddedConverter(embeddedListType, embeddedConverterDefinition);
            }
        }

        private static void RegisterEmbeddedConverter(Type embeddedType, Type embeddedConverterDefinition)
        {
            var embeddedConverterType = embeddedConverterDefinition.MakeGenericType(embeddedType);
            var converter = (ITypeHandler)Activator.CreateInstance(embeddedConverterType);

            SqlMapper.AddTypeHandler(embeddedType, converter);
        }
    }
}
