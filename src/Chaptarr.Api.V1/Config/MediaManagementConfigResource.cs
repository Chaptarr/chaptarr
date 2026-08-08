using Chaptarr.Http.REST;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.Config
{
    public class MediaManagementConfigResource : RestResource
    {
        public bool AutoUnmonitorPreviouslyDownloadedBooks { get; set; }
        public string RecycleBin { get; set; }
        public int RecycleBinCleanupDays { get; set; }
        public ProperDownloadTypes DownloadPropersAndRepacks { get; set; }
        public bool CreateEmptyAuthorFolders { get; set; }
        public bool CreateEmptyEbookAuthorFolders { get; set; }
        public bool DeleteEmptyFolders { get; set; }
        public FileDateType FileDate { get; set; }
        public bool WatchLibraryForChanges { get; set; }
        public bool GranularFileSystemScanning { get; set; }
        public RescanAfterRefreshType RescanAfterRefresh { get; set; }
        public AllowFingerprinting AllowFingerprinting { get; set; }
        public BookMatchingStrictness BookMatchingStrictness { get; set; }
        public bool UsePathAsTagsFallback { get; set; }
        public bool AutoAddMissingAuthorsFromCompletedDownloads { get; set; }
        public string DefaultAudiobookRootFolderPath { get; set; }
        public string DefaultEbookRootFolderPath { get; set; }

        public bool SetPermissionsLinux { get; set; }
        public string ChmodFolder { get; set; }
        public string ChownGroup { get; set; }

        public bool SkipFreeSpaceCheckWhenImporting { get; set; }
        public int MinimumFreeSpaceWhenImporting { get; set; }
        public bool CopyUsingHardlinks { get; set; }
        public bool ImportExtraFiles { get; set; }
        public string ExtraFileExtensions { get; set; }
    }

    public static class MediaManagementConfigResourceMapper
    {
        public static MediaManagementConfigResource ToResource(IConfigService model)
        {
            return new MediaManagementConfigResource
            {
                AutoUnmonitorPreviouslyDownloadedBooks = model.AutoUnmonitorPreviouslyDownloadedBooks,
                RecycleBin = model.RecycleBin,
                RecycleBinCleanupDays = model.RecycleBinCleanupDays,
                DownloadPropersAndRepacks = model.DownloadPropersAndRepacks,
                CreateEmptyAuthorFolders = model.CreateEmptyAuthorFolders,
                CreateEmptyEbookAuthorFolders = model.CreateEmptyEbookAuthorFolders,
                DeleteEmptyFolders = model.DeleteEmptyFolders,
                FileDate = model.FileDate,
                WatchLibraryForChanges = model.WatchLibraryForChanges,
                GranularFileSystemScanning = model.GranularFileSystemScanning,
                RescanAfterRefresh = model.RescanAfterRefresh,
                AllowFingerprinting = model.AllowFingerprinting,
                BookMatchingStrictness = model.BookMatchingStrictness,
                UsePathAsTagsFallback = model.UsePathAsTagsFallback,
                AutoAddMissingAuthorsFromCompletedDownloads = model.AutoAddMissingAuthorsFromCompletedDownloads,
                DefaultAudiobookRootFolderPath = model.DefaultAudiobookRootFolderPath,
                DefaultEbookRootFolderPath = model.DefaultEbookRootFolderPath,

                SetPermissionsLinux = model.SetPermissionsLinux,
                ChmodFolder = model.ChmodFolder,
                ChownGroup = model.ChownGroup,

                SkipFreeSpaceCheckWhenImporting = model.SkipFreeSpaceCheckWhenImporting,
                MinimumFreeSpaceWhenImporting = model.MinimumFreeSpaceWhenImporting,
                CopyUsingHardlinks = model.CopyUsingHardlinks,
                ImportExtraFiles = model.ImportExtraFiles,
                ExtraFileExtensions = model.ExtraFileExtensions,
            };
        }
    }
}
