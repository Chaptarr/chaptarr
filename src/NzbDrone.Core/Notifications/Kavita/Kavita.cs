using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Notifications.Kavita;

public class Kavita : NotificationBase<KavitaSettings>
{
    private readonly IKavitaService _kavitaService;
    private readonly ICached<HashSet<string>> _pendingFolders;

    public Kavita(IKavitaService kavitaService, ICacheManager cacheManager)
    {
        _kavitaService = kavitaService;
        _pendingFolders = cacheManager.GetCache<HashSet<string>>(GetType(), "pendingFolders");
    }

    public override string Link => "https://www.kavitareader.com/";

    public override void OnReleaseImport(BookDownloadMessage message)
    {
        Notify(message.BookFiles?.FirstOrDefault(file => file?.Path.IsNotNullOrWhiteSpace() == true)?.Path);
    }

    public override void OnBookDelete(BookDeleteMessage deleteMessage)
    {
        if (deleteMessage.DeletedFiles)
        {
            Notify(deleteMessage.Book.BookFiles?.FirstOrDefault(file => file?.Path.IsNotNullOrWhiteSpace() == true)?.Path);
        }
    }

    public override void OnBookFileDelete(BookFileDeleteMessage message)
    {
        Notify(message.BookFile?.Path);
    }

    public override void OnBookRetag(BookRetagMessage message)
    {
        Notify(message.BookFile?.Path);
    }

    public override string Name => "Kavita";

    public override bool HasPendingQueue
    {
        get
        {
            lock (_pendingFolders)
            {
                return _pendingFolders.Find(Definition.Id.ToString())?.Count > 0;
            }
        }
    }

    public override void ProcessQueue()
    {
        lock (_pendingFolders)
        {
            var key = Definition.Id.ToString();
            var folders = _pendingFolders.Find(key);
            if (Settings.Notify && folders != null)
            {
                foreach (var folder in folders.ToList())
                {
                    _kavitaService.Notify(Settings, folder);
                    folders.Remove(folder);
                }
            }

            _pendingFolders.Remove(key);
        }
    }

    public override ValidationResult Test()
    {
        var failures = new List<ValidationFailure>();

        failures.AddIfNotNull(_kavitaService.Test(Settings, "Success! Kavita has been successfully configured!"));

        return new ValidationResult(failures);
    }

    private void Notify(string filePath)
    {
        if (Settings.Notify && filePath.IsNotNullOrWhiteSpace())
        {
            var folder = Directory.GetParent(filePath)?.FullName;
            if (folder.IsNotNullOrWhiteSpace())
            {
                lock (_pendingFolders)
                {
                    _pendingFolders.Get(Definition.Id.ToString(), () => new HashSet<string>(PathEqualityComparer.Instance)).Add(folder);
                }
            }
        }
    }
}
