using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileDeletionObserverFixture
    {
        private static IEnumerable<TestCaseData> Observers()
        {
            var contracts = new[] { typeof(IHandle<BookFileDeletedEvent>), typeof(IHandleAsync<BookFileDeletedEvent>) };
            foreach (var type in new[] { typeof(BookFile).Assembly, typeof(BookFileController).Assembly }
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && contracts.Any(c => c.IsAssignableFrom(type)))
                .OrderBy(type => type.FullName))
            foreach (var reason in new[] { DeleteMediaFileReason.MissingFromDisk, DeleteMediaFileReason.NoLinkedEpisodes })
                yield return new TestCaseData(type, reason).SetName($"{type.Name}_{reason}_never_changes_disk");
        }

        [TestCaseSource(nameof(Observers))]
        public void every_observer_preserves_bytes_for_tracking_only_events(Type type, DeleteMediaFileReason reason)
        {
            var diskActions = new List<string>();
            var created = new List<object>();
            var author = new Author { Id = 7, Name = "Frank Herbert", Path = "/library/Frank Herbert", Tags = new HashSet<int>() };
            var book = new Book { Id = 8, AuthorId = author.Id, Author = author, Title = "Dune", MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Id = 9, BookId = book.Id, Book = book, Title = book.Title, Monitored = true };
            book.Editions = new List<Edition> { edition };
            var file = new BookFile { Id = 10, EditionId = edition.Id, Edition = edition, Author = author,
                Path = "/library/Frank Herbert/Dune/Dune.m4b", ReplicaPaths = new List<string> { "/library/copy.m4b" },
                MediaInfo = new MediaInfoModel(), Quality = new QualityModel(Quality.M4B), MediaType = "audiobook" };
            book.BookFiles = new List<BookFile> { file };

            object Empty(Type target)
            {
                if (target == typeof(void)) return null;
                if (target == typeof(string)) return string.Empty;
                if (target == typeof(Task)) return Task.CompletedTask;
                if (target.IsArray) return Array.CreateInstance(target.GetElementType(), 0);
                if (target.IsGenericType && typeof(IEnumerable).IsAssignableFrom(target))
                {
                    var list = typeof(List<>).MakeGenericType(target.GetGenericArguments()[0]);
                    if (target.IsAssignableFrom(list)) return Activator.CreateInstance(list);
                }
                if (target.IsValueType) return Activator.CreateInstance(target);
                if (target.IsInterface) return Dependency(target);
                return target.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(target) : null;
            }

            object Dependency(Type target)
            {
                if (target == typeof(Logger)) return LogManager.GetCurrentClassLogger();
                if (target == typeof(string)) return string.Empty;
                if (target.IsInterface)
                {
                    var proxy = (InterfaceProxy)DispatchProxy.Create(target, typeof(InterfaceProxy));
                    proxy.Handler = (method, args) =>
                    {
                        if (target == typeof(IDiskProvider) || target == typeof(IRecycleBinProvider))
                        {
                            if (method.Name.StartsWith("FileExists") || method.Name.StartsWith("FolderExists")) return true;
                            if (method.Name == "GetParentFolder") return System.IO.Path.GetDirectoryName((string)args[0]);
                            diskActions.Add(target.Name + "." + method.Name);
                        }
                        if (method.ReturnType == typeof(Author)) return author;
                        if (method.ReturnType == typeof(Book)) return book;
                        if (method.ReturnType == typeof(Edition)) return edition;
                        if (method.ReturnType == typeof(List<MetadataFile>)) return new List<MetadataFile>
                        {
                            new MetadataFile { Id = 11, BookFileId = file.Id, BookId = book.Id, AuthorId = author.Id, RelativePath = "Dune/Cover.jpg" }
                        };
                        if (method.ReturnType == typeof(List<OtherExtraFile>)) return new List<OtherExtraFile>
                        {
                            new OtherExtraFile { Id = 12, BookFileId = file.Id, BookId = book.Id, AuthorId = author.Id, RelativePath = "Dune/metadata.json" }
                        };
                        return Empty(method.ReturnType);
                    };
                    return proxy;
                }
                var constructor = target.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                var value = constructor.Invoke(constructor.GetParameters().Select(p => Dependency(p.ParameterType)).ToArray());
                created.Add(value);
                return value;
            }

            try
            {
                var observer = Dependency(type);
                var message = new BookFileDeletedEvent(file, reason);
                if (observer is IHandle<BookFileDeletedEvent> synchronous) synchronous.Handle(message);
                if (observer is IHandleAsync<BookFileDeletedEvent> asynchronous) asynchronous.HandleAsync(message);
                Assert.That(diskActions, Is.Empty, type.FullName + " may update records/UI, but tracking cleanup never authorizes disk changes");
            }
            finally
            {
                // API observers debounce broadcasts; do not leave task-owned timers running.
                foreach (var value in created)
                foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    if (field.GetValue(value) is Debouncer debouncer) debouncer.Pause();
            }
        }
    }
}
