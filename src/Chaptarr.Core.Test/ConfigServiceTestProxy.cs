using System;
using System.Reflection;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test
{
    public class ConfigServiceTestProxy : DispatchProxy
    {
        public BookMatchingStrictness BookMatchingStrictness { get; set; } = BookMatchingStrictness.Balanced;
        public bool UsePathAsTagsFallback { get; set; } = true;
        public bool AutoAddMissingAuthorsFromCompletedDownloads { get; set; }
        public string DefaultAudiobookRootFolderPath { get; set; } = string.Empty;
        public string DefaultEbookRootFolderPath { get; set; } = string.Empty;
        public bool ImportExtraFiles { get; set; }
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
                                            string defaultEbookRootFolderPath = "")
        {
            var service = DispatchProxy.Create<IConfigService, ConfigServiceTestProxy>();
            ((ConfigServiceTestProxy)service).BookMatchingStrictness = strictness;
            ((ConfigServiceTestProxy)service).UsePathAsTagsFallback = usePathAsTagsFallback;
            ((ConfigServiceTestProxy)service).DownloadPropersAndRepacks = downloadPropersAndRepacks;
            ((ConfigServiceTestProxy)service).AutoAddMissingAuthorsFromCompletedDownloads = autoAddMissingAuthorsFromCompletedDownloads;
            ((ConfigServiceTestProxy)service).DefaultAudiobookRootFolderPath = defaultAudiobookRootFolderPath;
            ((ConfigServiceTestProxy)service).DefaultEbookRootFolderPath = defaultEbookRootFolderPath;
            return service;
        }
    }
}
