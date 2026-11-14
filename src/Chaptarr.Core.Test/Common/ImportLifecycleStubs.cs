using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Common
{
    public static class ImportLifecycleStubs
    {
        public static IAuthorRepository NoPendingAuthorSetup() => InterfaceProxy.Proxy<IAuthorRepository>((method, _) =>
            method.Name == nameof(IAuthorRepository.GetDueInitialFormatSync) ? new List<int>() : throw new NotImplementedException(method.Name));

        public static IBookRepository NoBookRepositoryCalls() => InterfaceProxy.Proxy<IBookRepository>((method, _) =>
            throw new NotImplementedException(method.Name));

        public static global::NzbDrone.Core.History.IHistoryRepository NoHistoryRepositoryCalls() => InterfaceProxy.Proxy<global::NzbDrone.Core.History.IHistoryRepository>((method, _) =>
            throw new NotImplementedException(method.Name));

        public static global::NzbDrone.Core.MediaFiles.IConversionJobService NoConversionJobs() => InterfaceProxy.Proxy<global::NzbDrone.Core.MediaFiles.IConversionJobService>((method, args) =>
        {
            if (method.Name == nameof(global::NzbDrone.Core.MediaFiles.IConversionJobService.Get))
                return method.GetParameters()[0].ParameterType == typeof(string) ? null : new List<global::NzbDrone.Core.MediaFiles.ConversionJob>();
            if (method.Name == nameof(global::NzbDrone.Core.MediaFiles.IConversionJobService.GetCompletedConversion)) return null;
            if (method.Name == nameof(global::NzbDrone.Core.MediaFiles.IConversionJobService.GetCompletedConversions))
                return new List<global::NzbDrone.Core.MediaFiles.ConversionJobRequest>();
            if (method.Name != nameof(global::NzbDrone.Core.MediaFiles.IConversionJobService.RetainCompletedOutput)) throw new NotImplementedException(method.Name);
            return new global::NzbDrone.Core.MediaFiles.ConversionRetentionResult(global::NzbDrone.Core.MediaFiles.ConversionRetentionStatus.None, (string)args[2]);
        });

        // Isolated conversion-worker fixtures have no library files. Ownership fixtures supply their real inventory.
        public static global::NzbDrone.Core.MediaFiles.IMediaFileService NoLibraryMediaFiles() =>
            InterfaceProxy.Proxy<global::NzbDrone.Core.MediaFiles.IMediaFileService>((method, _) => method.Name switch
            {
                "GetFilePathOwnership" or "GetFilesByBook" => new List<global::NzbDrone.Core.MediaFiles.BookFile>(),
                "GetFileWithPath" => null,
                _ => throw new NotImplementedException(method.Name)
            });

        public static IQueueService EmptyQueue() => new EmptyDownloadQueue();

        private sealed class EmptyDownloadQueue : IQueueService
        {
            public List<NzbDrone.Core.Queue.Queue> GetQueue(bool includeConversionStatus = true) => new();
            public NzbDrone.Core.Queue.Queue Find(int id) => null;
            public void Remove(int id) { }
        }
    }
}
