using System;
using System.Reflection;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test
{
    public class ConfigServiceTestProxy : DispatchProxy
    {
        public BookMatchingStrictness BookMatchingStrictness { get; set; } = BookMatchingStrictness.Balanced;
        public int BookImportMinimumMatchPercent { get; set; } = 70;
        public bool BookshelfMatchCompatibility { get; set; }
        public bool BookshelfNGDatabaseMigrated { get; set; }
        public string BookshelfNGProviderDialect { get; set; } = string.Empty;
        public BookshelfNGCompatibilityProfile BookshelfNGMigrationProfile { get; set; } = new BookshelfNGCompatibilityProfile
        {
            Version = 1,
            ProviderDialect = "hc"
        };
        public int MinimumAge { get; set; }
        public int RssSyncInterval { get; set; }
        public bool UsePathAsTagsFallback { get; set; } = true;
        public bool AutoAddMissingAuthorsFromCompletedDownloads { get; set; }
        public bool AutoInitializeFormatSpecificAuthorPaths { get; set; }
        public string DefaultAudiobookRootFolderPath { get; set; } = string.Empty;
        public string DefaultEbookRootFolderPath { get; set; } = string.Empty;
        public bool ImportExtraFiles { get; set; }
        public bool MoveExtraFilesOnRename { get; set; }
        public bool KeepExistingLibraryFileOnImportConflict { get; set; }
        public ProperDownloadTypes DownloadPropersAndRepacks { get; set; } = ProperDownloadTypes.PreferAndUpgrade;
        public bool AudioProductionCustomFormatsSeeded { get; set; }
        public string SeededBuiltInCustomFormatKeys { get; set; } = string.Empty;
        public string MetadataServerUrl { get; set; } = "https://api2.chaptarr.com";

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == "get_BookMatchingStrictness")
            {
                return BookMatchingStrictness;
            }

            if (targetMethod?.Name == "set_BookMatchingStrictness")
            {
                BookMatchingStrictness = (BookMatchingStrictness)args[0];
                return null;
            }

            if (targetMethod?.Name == "get_BookImportMinimumMatchPercent")
            {
                return BookImportMinimumMatchPercent;
            }

            if (targetMethod?.Name == "set_BookImportMinimumMatchPercent")
            {
                BookImportMinimumMatchPercent = (int)args[0];
                return null;
            }

            if (targetMethod?.Name == "get_BookshelfMatchCompatibility")
            {
                return BookshelfMatchCompatibility;
            }

            if (targetMethod?.Name == "set_BookshelfMatchCompatibility")
            {
                BookshelfMatchCompatibility = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "get_BookshelfNGDatabaseMigrated")
            {
                return BookshelfNGDatabaseMigrated;
            }

            if (targetMethod?.Name == "get_BookshelfNGProviderDialect")
            {
                return BookshelfNGProviderDialect;
            }

            if (targetMethod?.Name == "get_BookshelfNGMigrationProfile")
            {
                return BookshelfNGMigrationProfile;
            }

            if (targetMethod?.Name == "get_MinimumAge")
            {
                return MinimumAge;
            }

            if (targetMethod?.Name == "get_RssSyncInterval")
            {
                return RssSyncInterval;
            }

            if (targetMethod?.Name == "get_DownloadPropersAndRepacks")
            {
                return DownloadPropersAndRepacks;
            }

            if (targetMethod?.Name == "set_DownloadPropersAndRepacks")
            {
                DownloadPropersAndRepacks = (ProperDownloadTypes)args[0];
                return null;
            }

            if (targetMethod?.Name == "get_UsePathAsTagsFallback")
            {
                return UsePathAsTagsFallback;
            }

            if (targetMethod?.Name == "get_AutoAddMissingAuthorsFromCompletedDownloads")
            {
                return AutoAddMissingAuthorsFromCompletedDownloads;
            }

            if (targetMethod?.Name == "get_AutoInitializeFormatSpecificAuthorPaths")
            {
                return AutoInitializeFormatSpecificAuthorPaths;
            }

            if (targetMethod?.Name == "get_DefaultAudiobookRootFolderPath")
            {
                return DefaultAudiobookRootFolderPath;
            }

            if (targetMethod?.Name == "get_DefaultEbookRootFolderPath")
            {
                return DefaultEbookRootFolderPath;
            }

            if (targetMethod?.Name == "get_ImportExtraFiles")
            {
                return ImportExtraFiles;
            }

            if (targetMethod?.Name == "get_MoveExtraFilesOnRename")
            {
                return MoveExtraFilesOnRename;
            }

            if (targetMethod?.Name == "get_MoveExtraFilesOnRename")
            {
                return MoveExtraFilesOnRename;
            }

            if (targetMethod?.Name == "get_KeepExistingLibraryFileOnImportConflict")
            {
                return KeepExistingLibraryFileOnImportConflict;
            }

            if (targetMethod?.Name == "get_AudioProductionCustomFormatsSeeded")
            {
                return AudioProductionCustomFormatsSeeded;
            }

            if (targetMethod?.Name == "get_SeededBuiltInCustomFormatKeys")
            {
                return SeededBuiltInCustomFormatKeys;
            }

            if (targetMethod?.Name == "get_MetadataServerUrl")
            {
                return MetadataServerUrl;
            }

            if (targetMethod?.Name == "set_UsePathAsTagsFallback")
            {
                UsePathAsTagsFallback = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_AutoAddMissingAuthorsFromCompletedDownloads")
            {
                AutoAddMissingAuthorsFromCompletedDownloads = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_AutoInitializeFormatSpecificAuthorPaths")
            {
                AutoInitializeFormatSpecificAuthorPaths = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_DefaultAudiobookRootFolderPath")
            {
                DefaultAudiobookRootFolderPath = (string)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_DefaultEbookRootFolderPath")
            {
                DefaultEbookRootFolderPath = (string)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_ImportExtraFiles")
            {
                ImportExtraFiles = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_MoveExtraFilesOnRename")
            {
                MoveExtraFilesOnRename = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_MoveExtraFilesOnRename")
            {
                MoveExtraFilesOnRename = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_KeepExistingLibraryFileOnImportConflict")
            {
                KeepExistingLibraryFileOnImportConflict = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_AudioProductionCustomFormatsSeeded")
            {
                AudioProductionCustomFormatsSeeded = (bool)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_SeededBuiltInCustomFormatKeys")
            {
                SeededBuiltInCustomFormatKeys = (string)args[0];
                return null;
            }

            if (targetMethod?.Name == "set_MetadataServerUrl")
            {
                MetadataServerUrl = (string)args[0];
                return null;
            }

            throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
        }

        public static IConfigService Create(BookMatchingStrictness strictness = BookMatchingStrictness.Balanced,
                                            bool usePathAsTagsFallback = true,
                                            ProperDownloadTypes downloadPropersAndRepacks = ProperDownloadTypes.PreferAndUpgrade,
                                            bool autoAddMissingAuthorsFromCompletedDownloads = false,
                                            string defaultAudiobookRootFolderPath = "",
                                            string defaultEbookRootFolderPath = "",
                                            bool autoInitializeFormatSpecificAuthorPaths = false,
                                            bool bookshelfMatchCompatibility = false,
                                            int bookImportMinimumMatchPercent = 70)
        {
            var service = DispatchProxy.Create<IConfigService, ConfigServiceTestProxy>();
            ((ConfigServiceTestProxy)service).BookMatchingStrictness = strictness;
            ((ConfigServiceTestProxy)service).UsePathAsTagsFallback = usePathAsTagsFallback;
            ((ConfigServiceTestProxy)service).DownloadPropersAndRepacks = downloadPropersAndRepacks;
            ((ConfigServiceTestProxy)service).AutoAddMissingAuthorsFromCompletedDownloads = autoAddMissingAuthorsFromCompletedDownloads;
            ((ConfigServiceTestProxy)service).DefaultAudiobookRootFolderPath = defaultAudiobookRootFolderPath;
            ((ConfigServiceTestProxy)service).DefaultEbookRootFolderPath = defaultEbookRootFolderPath;
            ((ConfigServiceTestProxy)service).AutoInitializeFormatSpecificAuthorPaths = autoInitializeFormatSpecificAuthorPaths;
            ((ConfigServiceTestProxy)service).BookshelfMatchCompatibility = bookshelfMatchCompatibility;
            ((ConfigServiceTestProxy)service).BookImportMinimumMatchPercent = bookImportMinimumMatchPercent;
            return service;
        }
    }
}
