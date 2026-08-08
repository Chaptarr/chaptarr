using System;
using System.Threading;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public class ImportProgress
    {
        public int TotalFolders { get; set; }
        public int ProcessedFolders { get; set; }
        public string CurrentAuthor { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Elapsed => DateTime.UtcNow - StartTime;
        public double PercentComplete => TotalFolders > 0 ? (ProcessedFolders * 100.0) / TotalFolders : 0;
    }

    public class ImportProgressEvent : IEvent
    {
        public ImportProgress Progress { get; }

        public ImportProgressEvent(ImportProgress progress)
        {
            Progress = progress;
        }
    }

    public interface ILightweightImportTracker
    {
        void StartImport(int totalFolders);
        void UpdateProgress(int foldersProcessed, string currentAuthor);
        CancellationToken GetCancellationToken();
        void CancelImport();
        void EndImport();
        ImportProgress GetCurrentProgress();
    }

    public class LightweightImportTracker : ILightweightImportTracker
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private volatile ImportProgress _currentProgress;
        private volatile CancellationTokenSource _cancellationSource;

        public LightweightImportTracker(IEventAggregator eventAggregator, Logger logger)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void StartImport(int totalFolders)
        {
            _logger.Debug("Starting import tracking for {0} folders", totalFolders);
            _currentProgress = new ImportProgress
            {
                TotalFolders = totalFolders,
                ProcessedFolders = 0,
                StartTime = DateTime.UtcNow
            };
            _cancellationSource = new CancellationTokenSource();

            // Publish initial progress event
            _eventAggregator.PublishEvent(new ImportProgressEvent(_currentProgress));
        }

        public void UpdateProgress(int foldersProcessed, string currentAuthor)
        {
            if (_currentProgress == null)
            {
                _logger.Warn("UpdateProgress called without active import");
                return;
            }

            _currentProgress.ProcessedFolders = foldersProcessed;
            _currentProgress.CurrentAuthor = currentAuthor;

            _logger.Debug("Import progress: {0}/{1} folders ({2:F1}%) - Current author: {3}",
                foldersProcessed, _currentProgress.TotalFolders,
                _currentProgress.PercentComplete, currentAuthor);

            // Publish progress event for UI
            _eventAggregator.PublishEvent(new ImportProgressEvent(_currentProgress));
        }

        public CancellationToken GetCancellationToken()
        {
            return _cancellationSource?.Token ?? CancellationToken.None;
        }

        public void CancelImport()
        {
            _logger.Debug("Import cancellation requested");
            _cancellationSource?.Cancel();
        }

        public void EndImport()
        {
            if (_currentProgress != null)
            {
                _logger.Debug("Import completed: {0} folders processed in {1}",
                    _currentProgress.ProcessedFolders, _currentProgress.Elapsed);
            }

            _currentProgress = null;
            _cancellationSource?.Dispose();
            _cancellationSource = null;
        }

        public ImportProgress GetCurrentProgress()
        {
            return _currentProgress;
        }
    }
}
