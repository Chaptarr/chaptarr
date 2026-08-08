using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.CustomFormats
{
    public interface ICustomFormatService
    {
        void Update(CustomFormat customFormat);
        CustomFormat Insert(CustomFormat customFormat);
        List<CustomFormat> All();
        CustomFormat GetById(int id);
        void Delete(int id);
    }

    public class CustomFormatService : ICustomFormatService,
                                       IHandle<ApplicationStartedEvent>
    {
        private readonly ICustomFormatRepository _formatRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;
        private readonly ICached<Dictionary<int, CustomFormat>> _cache;
        private readonly object _ensureBuiltInsLock = new object();
        private bool _builtInsEnsured;

        public CustomFormatService(ICustomFormatRepository formatRepository,
                                   ICacheManager cacheManager,
                                   IConfigService configService,
                                   IEventAggregator eventAggregator)
        {
            _formatRepository = formatRepository;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _cache = cacheManager.GetCache<Dictionary<int, CustomFormat>>(typeof(CustomFormat), "formats");
        }

        private Dictionary<int, CustomFormat> AllDictionary()
        {
            EnsureBuiltInFormats();
            return _cache.Get("all", () => _formatRepository.All().ToDictionary(m => m.Id));
        }

        public List<CustomFormat> All()
        {
            return AllDictionary().Values.ToList();
        }

        public CustomFormat GetById(int id)
        {
            return AllDictionary()[id];
        }

        public void Update(CustomFormat customFormat)
        {
            var previousAppliesTo = _formatRepository.Get(customFormat.Id).AppliesTo;
            _formatRepository.Update(customFormat);
            _cache.Clear();
            _eventAggregator.PublishEvent(new CustomFormatUpdatedEvent(customFormat, previousAppliesTo));
        }

        public CustomFormat Insert(CustomFormat customFormat)
        {
            // Add to DB then insert into profiles
            var result = _formatRepository.Insert(customFormat);
            _cache.Clear();

            _eventAggregator.PublishEvent(new CustomFormatAddedEvent(result));

            return result;
        }

        public void Delete(int id)
        {
            var format = _formatRepository.Get(id);

            // Remove from profiles before removing from DB
            _eventAggregator.PublishEvent(new CustomFormatDeletedEvent(format));

            _formatRepository.Delete(id);
            _cache.Clear();
        }

        private void EnsureBuiltInFormats()
        {
            if (_builtInsEnsured)
            {
                return;
            }

            var insertedFormats = new List<CustomFormat>();

            lock (_ensureBuiltInsLock)
            {
                if (_builtInsEnsured)
                {
                    return;
                }

                var seededKeys = ParseSeededKeys(_configService.SeededBuiltInCustomFormatKeys);
                var seededKeysChanged = false;

                if (seededKeys.Count == 0 && _configService.AudioProductionCustomFormatsSeeded)
                {
                    seededKeys.Add(BuiltInCustomFormats.DramatizedAudioKey);
                    seededKeys.Add(BuiltInCustomFormats.StandardAudioKey);
                    seededKeysChanged = true;
                }

                var existing = _formatRepository.All().ToList();

                foreach (var unkeyed in existing.Where(format => format.BuiltInKey == null).ToList())
                {
                    if (!BuiltInCustomFormats.TryGetRetiredBuiltInKeyForUnkeyed(unkeyed, out var retiredKey) ||
                        !seededKeys.Contains(retiredKey))
                    {
                        continue;
                    }

                    unkeyed.BuiltInKey = retiredKey;
                    _formatRepository.Update(unkeyed);
                    _cache.Clear();
                }

                foreach (var builtIn in BuiltInCustomFormats.All())
                {
                    var existingBuiltIn = existing.FirstOrDefault(f => string.Equals(f.BuiltInKey, builtIn.BuiltInKey, StringComparison.OrdinalIgnoreCase));
                    if (existingBuiltIn != null)
                    {
                        var changed = MigrateLegacyBuiltInName(existingBuiltIn, builtIn);
                        if (existingBuiltIn.AppliesTo != builtIn.AppliesTo)
                        {
                            existingBuiltIn.AppliesTo = builtIn.AppliesTo;
                            changed = true;
                        }

                        if (changed)
                        {
                            _formatRepository.Update(existingBuiltIn);
                            _cache.Clear();
                        }

                        seededKeysChanged = seededKeys.Add(builtIn.BuiltInKey) || seededKeysChanged;
                        continue;
                    }

                    if (seededKeys.Contains(builtIn.BuiltInKey))
                    {
                        continue;
                    }

                    var matchingExisting = existing.FirstOrDefault(f => IsMatchingUnkeyedBuiltIn(f, builtIn));
                    if (matchingExisting != null)
                    {
                        matchingExisting.BuiltInKey = builtIn.BuiltInKey;
                        matchingExisting.AppliesTo = builtIn.AppliesTo;
                        MigrateLegacyBuiltInName(matchingExisting, builtIn);
                        _formatRepository.Update(matchingExisting);
                        _cache.Clear();
                        seededKeys.Add(builtIn.BuiltInKey);
                        seededKeysChanged = true;
                        continue;
                    }

                    var inserted = _formatRepository.Insert(builtIn);
                    existing.Add(inserted);
                    insertedFormats.Add(inserted);
                    seededKeys.Add(builtIn.BuiltInKey);
                    seededKeysChanged = true;
                    _cache.Clear();
                }

                if (seededKeysChanged)
                {
                    _configService.SeededBuiltInCustomFormatKeys = SerializeSeededKeys(seededKeys);
                }

                _configService.AudioProductionCustomFormatsSeeded = true;
                _builtInsEnsured = true;
            }

            foreach (var inserted in insertedFormats)
            {
                _eventAggregator.PublishEvent(new CustomFormatAddedEvent(inserted, BuiltInCustomFormats.GetDefaultAudiobookProfileScore(inserted)));
            }
        }

        private static HashSet<string> ParseSeededKeys(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string SerializeSeededKeys(HashSet<string> keys)
        {
            return string.Join(",", keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        }

        private static bool MigrateLegacyBuiltInName(CustomFormat existing, CustomFormat current)
        {
            var legacyNames = BuiltInCustomFormats.GetLegacyNames(existing?.BuiltInKey);
            if (!legacyNames.Contains(existing?.Name, StringComparer.Ordinal))
            {
                return false;
            }

            existing.Name = current.Name;
            foreach (var specification in existing.Specifications ?? new List<ICustomFormatSpecification>())
            {
                if (legacyNames.Contains(specification.Name, StringComparer.Ordinal))
                {
                    specification.Name = current.Name;
                }
            }

            return true;
        }

        private static bool IsMatchingUnkeyedBuiltIn(CustomFormat existing, CustomFormat builtIn)
        {
            return existing.BuiltInKey == null &&
                   (string.Equals(existing.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase) ||
                    BuiltInCustomFormats.GetLegacyNames(builtIn.BuiltInKey)
                        .Any(name => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        public void Handle(ApplicationStartedEvent message)
        {
            EnsureBuiltInFormats();
        }
    }
}
