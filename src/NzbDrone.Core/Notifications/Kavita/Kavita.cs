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
    private readonly ICached<object> _queueLocks;
    private readonly IPendingNotificationRepository _pending;

    public Kavita(IKavitaService kavitaService, ICacheManager cacheManager, IPendingNotificationRepository pending)
    {
        _kavitaService = kavitaService;
        _queueLocks = cacheManager.GetCache<object>(GetType(), "queueLocks");
        _pending = pending;
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

    public override bool HasPendingQueue => _pending.HasPending(Definition.Id);

    public override void ProcessQueue()
    {
        lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
        {
            foreach (var item in _pending.GetPending(Definition.Id))
            {
                if (Settings.Notify) _kavitaService.Notify(Settings, item.Path);
                _pending.Complete(new[] { item });
            }
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
                lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
                {
                    _pending.Enqueue(Definition.Id, folder, true);
                }
            }
        }
    }
}
