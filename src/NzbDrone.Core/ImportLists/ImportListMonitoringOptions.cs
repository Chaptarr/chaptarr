using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListMonitoringOptions : IEmbeddedDocument
    {
        private readonly HashSet<string> _specified = new();
        private bool _addMissingAuthors = true;
        private bool _resumePausedAuthors;
        private bool? _audiobookMonitored;
        private bool? _ebookMonitored;
        private bool? _audiobookMonitorListedBooks;
        private bool? _ebookMonitorListedBooks;
        private MonitorTypes? _audiobookMonitor;
        private MonitorTypes? _ebookMonitor;
        private NewItemMonitorTypes? _audiobookMonitorNewItems;
        private NewItemMonitorTypes? _ebookMonitorNewItems;

        public bool AddMissingAuthors { get => _addMissingAuthors; set => Set(ref _addMissingAuthors, value); }
        public bool ResumePausedAuthors { get => _resumePausedAuthors; set => Set(ref _resumePausedAuthors, value); }
        public bool? AudiobookMonitored { get => _audiobookMonitored; set => Set(ref _audiobookMonitored, value); }
        public bool? EbookMonitored { get => _ebookMonitored; set => Set(ref _ebookMonitored, value); }
        public bool? AudiobookMonitorListedBooks { get => _audiobookMonitorListedBooks; set => Set(ref _audiobookMonitorListedBooks, value); }
        public bool? EbookMonitorListedBooks { get => _ebookMonitorListedBooks; set => Set(ref _ebookMonitorListedBooks, value); }
        public MonitorTypes? AudiobookMonitor { get => _audiobookMonitor; set => Set(ref _audiobookMonitor, value); }
        public MonitorTypes? EbookMonitor { get => _ebookMonitor; set => Set(ref _ebookMonitor, value); }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get => _audiobookMonitorNewItems; set => Set(ref _audiobookMonitorNewItems, value); }
        public NewItemMonitorTypes? EbookMonitorNewItems { get => _ebookMonitorNewItems; set => Set(ref _ebookMonitorNewItems, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string property = null)
        {
            field = value;
            _specified.Add(property);
        }

        public bool WasSpecified(string property) => _specified.Contains(property);

        public bool GetMonitorListedBooks(BookMediaType mediaType, ImportListMonitorType legacyPolicy)
        {
            var policy = mediaType == BookMediaType.Audiobook ? AudiobookMonitor : EbookMonitor;
            var listed = mediaType == BookMediaType.Audiobook ? AudiobookMonitorListedBooks : EbookMonitorListedBooks;
            return policy == MonitorTypes.SpecificBook || (listed ?? legacyPolicy != ImportListMonitorType.None);
        }

        public ImportListMonitorType? GetLegacyMonitorType(BookMediaType mediaType, ImportListMonitorType legacyPolicy)
        {
            var policy = mediaType == BookMediaType.Audiobook ? AudiobookMonitor : EbookMonitor;
            var listed = GetMonitorListedBooks(mediaType, legacyPolicy);
            return policy switch
            {
                MonitorTypes.SpecificBook => ImportListMonitorType.SpecificBook,
                MonitorTypes.All when listed => ImportListMonitorType.EntireAuthor,
                MonitorTypes.None => listed ? ImportListMonitorType.SpecificBook : ImportListMonitorType.None,
                _ => null
            };
        }

        // An omitted nested property preserves the saved value; an explicit null restores inheritance.
        public ImportListMonitoringOptions MergeWith(ImportListMonitoringOptions existing)
        {
            existing ??= new ImportListMonitoringOptions();
            return new ImportListMonitoringOptions
            {
                AddMissingAuthors = WasSpecified(nameof(AddMissingAuthors)) ? AddMissingAuthors : existing.AddMissingAuthors,
                ResumePausedAuthors = WasSpecified(nameof(ResumePausedAuthors)) ? ResumePausedAuthors : existing.ResumePausedAuthors,
                AudiobookMonitored = WasSpecified(nameof(AudiobookMonitored)) ? AudiobookMonitored : existing.AudiobookMonitored,
                EbookMonitored = WasSpecified(nameof(EbookMonitored)) ? EbookMonitored : existing.EbookMonitored,
                AudiobookMonitorListedBooks = WasSpecified(nameof(AudiobookMonitorListedBooks)) ? AudiobookMonitorListedBooks : existing.AudiobookMonitorListedBooks,
                EbookMonitorListedBooks = WasSpecified(nameof(EbookMonitorListedBooks)) ? EbookMonitorListedBooks : existing.EbookMonitorListedBooks,
                AudiobookMonitor = WasSpecified(nameof(AudiobookMonitor)) ? AudiobookMonitor : existing.AudiobookMonitor,
                EbookMonitor = WasSpecified(nameof(EbookMonitor)) ? EbookMonitor : existing.EbookMonitor,
                AudiobookMonitorNewItems = WasSpecified(nameof(AudiobookMonitorNewItems)) ? AudiobookMonitorNewItems : existing.AudiobookMonitorNewItems,
                EbookMonitorNewItems = WasSpecified(nameof(EbookMonitorNewItems)) ? EbookMonitorNewItems : existing.EbookMonitorNewItems
            };
        }
    }
}
