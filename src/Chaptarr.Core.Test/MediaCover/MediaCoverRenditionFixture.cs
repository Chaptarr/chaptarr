using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Http;
using Chaptarr.Http.Frontend;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using Chaptarr.Http.Frontend.Mappers;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using HttpRequest = NzbDrone.Common.Http.HttpRequest;
using HttpResponse = NzbDrone.Common.Http.HttpResponse;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverRenditionFixture
    {
        [TestCase("image/png")]
        [TestCase("text/plain")]
        [TestCase("application/octet-stream")]
        [TestCase(null)]
        public void cover_format_is_detected_from_bytes_not_the_cdn_content_type(string contentType)
        {
            var image = BuildPngBytes(2, 2, new Rgba32(30, 120, 210));
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(image, contentType));
        }

        [TestCase("text/html")]
        [TestCase("image/jpeg")]
        [TestCase(null)]
        public void an_error_page_is_rejected_even_when_it_claims_to_be_an_image(string contentType)
        {
            Assert.Throws<InvalidDataException>(() => MediaCoverRendition.ValidateImageResponse(
                Encoding.UTF8.GetBytes("<html>CDN unavailable</html>"), contentType));
        }

        private const string KnownPlaceholderJpegHash = "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e";
        private const string KnownPlaceholderWebpHash = "db25714c302dcc8ccca766d734947df2931fcc74cbed1656ad2eb470613db981";
        private const string VerifiedRealImageHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly string ConfigRoot = @"C:\config".AsOsAgnostic();
        internal const string MascotWebpBase64 = "UklGRiAKAABXRUJQVlA4WAoAAAAIAAAADQEADQEAVlA4IEAJAACQRACdASoOAQ4BPpFGnUolpCMhp/c5qLASCWNu4WeQ4h1f6TXlfOc2t0RMMfq8ufm7qXfnb2AOe35hf2k9an0Rf4f1AP7B1EHoUdLZ/d8k79MbvNezbj7O5YsO/lYLiJ5bwfL7Sf2x7I++8RFsJO5G2vmv3a0Lv8fqUghl9CqGlXRuZEZmejOuy/t5NCcZX5Mz6mj/aPjBydT1FXo5jed+6VDPhfHjGjVe+Awu634nb2Haa/SiibiOZSiqrjGqni7lPKbOZNXiHJEV510Okyv3nfhDGna4gOkFVv83PPKNO3LqhLAHwuqEXtvLqntpbwf0dJvp8eDLmN4xpWlYS8+E6V5ygvHhnELIyqpat99+h8Mb+MsyihgPm4F07CKdvIdAmivK60QgTgELCiaimYJg0yTyvewu4keIVrWhuvnx4x0jRHwD88c71AK7Ns/MoxN9Tf6JJBqwnjWB5/b8VaoQiqhFiRsqRBBPPew0UX9nwDqdlQoYn9vT6TXx9HCr8lks8PQnU5fc6v7en01VSYYxU22j9SMKssSPi/9FKtbx0xHrKmOkbKWzvWFwKsjR+MjsN6kLVcBavT6apdSi12+ef2UptJnQOxnP6WaQOrvEFJSElIpsqQ80sR2fownZEmLAXB/t0aTDr0TnUfC9lFZZxVUbx4uaxJyYrmpYnENTVwL3UAsy3gNZ6Zb3/yLlhZNIt9U87zPafQgqOPGpkWb/7rEqUkrnnAhnHXZ4AAD+/Q2w4croBAvXchtLIcVCl9UFwNXsRRWAmkJGgxxUN1/3nDbzUwJoPaQ2qbZCf9/ZwLq9nBnphl75/ipcCKFhi5Wm1QEOoSEn5FheeTq44eLr5DYryPY9TVFUq2rsEiHpGQTM4TFDmPvFDsACKXhtChegZWOImU3O81uohkz75fohihRrM7/pLnpu0AiiDgEUMHIb1t3Jl9U3ciDDgm+QmC/zOWiHnHEs8y5FyrcmGGRcmGAuG5KEFuAYmYoXCdr1TfGeTN3l5iWkX1Dl8+T9acV02vENwq9heCc1z4tFmo79dIvKZpue/YoRnXA6iVAzu60UAWJE6r4HFrp89LFZj9xdsHI5LTVml512QzYeIwO+vxNHeyj4ZAO5pq3xCzvAy5layhLlUTrFS/A2SD8RMmb3UHh7nAe4uEx7sbDFvvYFdV2sH2P0fUcrI2stdIX42VuugRGRb/gfqCJtBqQCfP2uHUM4Smn/YVBGccqHN5BGiI9FxRlfCdZ5B97eWip2FqXFR6rqOgU5qWJDPvQH1t+ZVACoRrzron1/j7SwsZiBD57AfuRq54y/nm5HC4Mp5ZxWjOu586EuRhbQR6xx/QCo0LTVAUl42tZVK7/RcqZXfSZX0Jm7A/Qj7LBP2SEUIVphZR9Dz81ET2nBoFjh6FFv4XDzDlvyaHHX7b6kB2oo8V4jHCt7k5QBFlzqKtwTcWQP06c0yjhGayTqFsh4gxXT8Ux9SMkdX3wDsvUaWtK/1tyIAfI6ZTEh48upEAkJr7NOWnZKYrpiWknd0fsPKSLQNbkuEUHpMxHXMSVibzasT8FK+u5Jkrqr1wLZkqcI7PqiurKMTkPP4oxKWxXncCtXRotaJcs4e3qKSZoUCg8YO/i+wFrgeQndk/9hmB6FeF1tYYLqo/CTQWQvglWvJasWC2IyYOfwgYuAOf2phCj88su0+gaFG3WUIXl/be61EUNSU3b1c7aPR9NVvmcD02RAMRmJ7y90wVs3fvgaguQIa6oWSYUmSJ+YdaLiVrxgQSe2zMsBPNPTGJmQskfXjZUMBy17j1HBlvNaXB6tCYiJd6hYEzWNu5/L41ToNDPRlXZnj/Z5VLXbkHK316tRL9jxUIkzSYDMHhwEoCKkJIBCoBiOSfNFpJRXbOOrlJsk9Qeriwwn0F2chcxFuGkxVNn4ogWuMLh79yxdbkk+MUTIKhq9QAmvwJIPhuk4//yPvc862DP3dmVfEDtTXf+EE9qNKtxt65dLDa2fGSKy35UNDiOyMIFhmIIQixqfIYSYNot6V/DAlJm+OGQL+//IiJyG0Iv2ULYA/T5MGBHcFxfiLopOrLVlCrVHOR/XLFVShp4pSYlhs6xYeuLdA1ZMS/B9jaInAQ6zKp82rAufWD9NlI7cuR28WOCkADaA/DC5Q1byQOdlsbDqKUqE5wrZVwYjm+RWCOJsTUNpuP4Vo9156ic7nfw5+AQENOQHH6Ai5jE4SGHlJH5aHOdCPBeZM+5+j1+3u3QgcRzyYE0mwJfDkLDGQ+csIQQXXHH0Gmzb72QQe/zOdYWledQm/fhfzgfPnPRjtOuXAzgATZDq/ECUzJs6lLe0tKvGPNjP/mdrEkOYdouK0cYUkUXvSIT+O2lUolSVN5WHXV8LZcO74MLl6q9PKBGKmltjF7yZS7BErBtUDOHAYWUTxfIkyUaoCXAvz1knM7L4KE1DXTsvLJDeMmqTo4x1z2pXBQuq5XXXJ4SrPDaRjdrADYTV3yh5pZiewudf0YUdtdCXIm4UNK3TaRCeIaWqSDC5RZ+a6BG3SzGmOvOKLKX2fFvaViHwTAz4YogsP1P/sinkrtpGNcfSsPrHTHYVfzQBFkjvnGRqIc9DPkyG73duq1Ycx0FssHP7kIrs2PbX0ot/30Tbl3GnB/CR9iqoD3VNdydypBiZ50sYdbIAywTQE+C1Kef0KH8K8yvRD5/nhNAJDpWktAl7aYMMeNEYcDj4aizASx+2E2Tn9ilb5/vfLqzC3PNBwUHsRujQt9RtOmSwOosYqvR3KQG75YJtAMADAfzczXYPB3N2XpKI2plKVLuRaKx3f68uSjuVq4vh3326u1dq3La6AgVZ2CFlroDPmwuXQVPFBSAqZ4UBetH9nG4UuQ/oi4OkX3ykBz+S/Cp5JT2QQ0GjiiU2cEBNyXqbIP8EXIPCIhpZK3ghpLOXwCPTMhTFR37QCqGRKg5kgqb55vBlqmnFQZobRh1bHuIl3M6zcnvWcQoH0CtbTEYwkTzsoc8BfNRKAB2hHaey1PzZYRRZmBP7xggb/D0OwIjb/bhV2w/Jnk+LSpnDc1fSU89P7BDTbJIq6fZcz4FHCwEPtF6tFraLkJ/5r++h74fEpircX7yEEitAGIsvmHSP3UB2xPEcoNlEViaWlUfHg5VnG0mAAAAARVhJRroAAABFeGlmAABJSSoACAAAAAYAEgEDAAEAAAABAAAAGgEFAAEAAABWAAAAGwEFAAEAAABeAAAAKAEDAAEAAAACAAAAEwIDAAEAAAABAAAAaYcEAAEAAABmAAAAAAAAADp+BQDoAwAAOn4FAOgDAAAGAACQBwAEAAAAMDIxMAGRBwAEAAAAAQIDAACgBwAEAAAAMDEwMAGgAwABAAAA//8AAAKgBAABAAAADgEAAAOgBAABAAAADgEAAAAAAAA=";

        private class AppFolderProxy : DispatchProxy
        {
            public string AppDataFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_AppDataFolder" => AppDataFolder,
                    "get_TempFolder" => Path.GetTempPath(),
                    "get_StartUpFolder" => AppDataFolder,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private class ConfigFileProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "get_UrlBase"
                    ? string.Empty
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class CoverScopeConfigProxy : DispatchProxy
        {
            public CoverDownloadScope Scope { get; set; }
            public Action OnRead;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_CoverDownloadScope" => ReadScope(),
                    "get_MetadataServerUrl" => "https://images.example",
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private CoverDownloadScope ReadScope()
            {
                OnRead?.Invoke();
                return Scope;
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> TextByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> BinaryByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> DeletedFolders { get; } = new();
            public Dictionary<string, int> CallCounts { get; } = new(StringComparer.Ordinal);
            public bool RefusePublication;
            public HashSet<string> MissingFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Action<string> OnFolderExists { get; set; }
            private bool FolderExistsWithHook(string path)
            {
                var exists = !MissingFolders.Contains(path);
                OnFolderExists?.Invoke(path);
                return exists;
            }
            public Action<string> BeforeOpen;
            public Action<string> BeforeDelete;
            public Action<string> BeforeReadText;
            public Action<string> BeforeWriteText;
            public Action<string> AfterWriteText;
            public Action<string> AfterPublication;

            public int GetCallCount(string methodName)
            {
                return CallCounts.GetValueOrDefault(methodName);
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                CallCounts[targetMethod.Name] = GetCallCount(targetMethod.Name) + 1;
                var path = args?.FirstOrDefault() as string;
                if (targetMethod.Name == nameof(IDiskProvider.ReadAllText)) BeforeReadText?.Invoke(path);

                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FileExists) => ExistingPaths.Contains(path),
                    nameof(IDiskProvider.GetFileSize) => BinaryByPath.TryGetValue(path, out var bytes) ? bytes.LongLength : ExistingPaths.Contains(path) ? 1024L : 0L,
                    nameof(IDiskProvider.FileGetLastWrite) => new DateTime(1234),
                    nameof(IDiskProvider.FolderExists) => FolderExistsWithHook(path),
                    nameof(IDiskProvider.GetFiles) => ExistingPaths.Where(existing =>
                        string.Equals(Path.GetDirectoryName(existing), path, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    nameof(IDiskProvider.EnsureFolder) => null,
                    nameof(IDiskProvider.ReadAllText) => TextByPath.TryGetValue(path, out var value) ? value : null,
                    nameof(IDiskProvider.OpenReadStream) => Open(path),
                    nameof(IDiskProvider.WriteAllText) => WriteText(path, (string)args[1]),
                    nameof(IDiskProvider.SaveStream) => Save((string)args[1], (Stream)args[0]),
                    nameof(IDiskProvider.WriteFileAtomically) => WriteGeneratedFile(path, (Action<string>)args[1]),
                    nameof(IDiskProvider.MoveFile) => Move((string)args[0], (string)args[1]),
                    nameof(IDiskProvider.CopyFile) => Copy((string)args[0], (string)args[1]),
                    nameof(IDiskProvider.DeleteFile) => Delete(path),
                    nameof(IDiskProvider.DeleteFolder) => DeleteFolder(path),
                    nameof(IDiskProvider.FileSetLastWriteTime) => null,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private object WriteGeneratedFile(string path, Action<string> write)
            {
                var staged = Path.Combine(Path.GetDirectoryName(path), DiskProviderBase.CurrentTemporaryWritePrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                try
                {
                    write(staged);
                    if (RefusePublication) throw new IOException("Fixture publication refused");
                    Move(staged, path);
                    AfterPublication?.Invoke(path);
                }
                finally
                {
                    Delete(staged);
                }
                return null;
            }

            private FileStream Open(string path)
            {
                BeforeOpen?.Invoke(path);
                var temporaryPath = Path.GetTempFileName();
                File.WriteAllBytes(temporaryPath, BinaryByPath.TryGetValue(path, out var bytes) ? bytes : Array.Empty<byte>());
                return new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.DeleteOnClose);
            }

            private object WriteText(string path, string value)
            {
                BeforeWriteText?.Invoke(path);
                TextByPath[path] = value;
                ExistingPaths.Add(path);
                AfterWriteText?.Invoke(path);
                return null;
            }

            private object Move(string source, string destination)
            {
                ExistingPaths.Remove(source);
                ExistingPaths.Add(destination);
                if (TextByPath.Remove(source, out var text)) TextByPath[destination] = text;
                if (BinaryByPath.Remove(source, out var bytes))
                {
                    BinaryByPath[destination] = bytes;
                }
                return null;
            }

            private object Save(string path, Stream stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                BinaryByPath[path] = buffer.ToArray();
                ExistingPaths.Add(path);
                return null;
            }

            private object Copy(string source, string destination)
            {
                BinaryByPath[destination] = BinaryByPath[source].ToArray();
                ExistingPaths.Add(destination);
                return null;
            }

            private object Delete(string path)
            {
                BeforeDelete?.Invoke(path);
                ExistingPaths.Remove(path);
                TextByPath.Remove(path);
                BinaryByPath.Remove(path);
                return null;
            }

            private object DeleteFolder(string path)
            {
                DeletedFolders.Add(path);
                foreach (var existing in ExistingPaths.Where(existing => existing.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                    Delete(existing);
                return null;
            }
        }

        private class ImageHttpClientProxy : DispatchProxy
        {
            public byte[] ResponseData { get; set; } = Array.Empty<byte>();
            public DateTime? LastModified { get; set; }
            public HttpStatusCode? FailureStatus { get; set; }
            public Func<HttpRequest, HttpStatusCode?> FailureForRequest { get; set; }
            public Exception Failure { get; set; }
            public Queue<byte[]> ResponseQueue { get; } = new();
            public List<string> RequestedUrls { get; } = new();
            public int Calls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHttpClient.Get) && args?[0] is HttpRequest request)
                {
                    Calls++;
                    RequestedUrls.Add(request.Url.FullUri);
                    if (Failure != null)
                    {
                        throw Failure;
                    }
                    var failureStatus = FailureForRequest?.Invoke(request) ?? FailureStatus;
                    if (failureStatus.HasValue)
                    {
                        throw new HttpException(new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), failureStatus.Value));
                    }
                    var responseData = ResponseQueue.Count > 0 ? ResponseQueue.Dequeue() : ResponseData;
                    return new HttpResponse(request, new HttpHeader { LastModified = LastModified }, responseData);
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DeferredCoverProxy : DispatchProxy
        {
            public readonly DeferredCoverService State = new(LogManager.GetCurrentClassLogger(), new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            public bool ShouldDefer { get => State.IsCoverDownloadDeferred; set => State.IsCoverDownloadDeferred = value; }
            public List<int> MarkedBookIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_IsCoverDownloadDeferred" => ShouldDefer,
                    "set_IsCoverDownloadDeferred" => SetDeferred((bool)args[0]),
                    nameof(IDeferredCoverService.DeferRepairIfImporting) => State.DeferRepairIfImporting(),
                    nameof(IDeferredCoverService.TakePendingRepair) => State.TakePendingRepair(),
                    nameof(IDeferredCoverService.RequeueRepair) => RequeueRepair(),
                    nameof(IDeferredCoverService.TakePendingBookIds) => State.TakePendingBookIds(),
                    nameof(IDeferredCoverService.MarkBookForCoverDownload) => Mark((int)args[0]),
                    nameof(IDeferredCoverService.RequeueBooksForCoverDownload) => RecordRequeue((IEnumerable<int>)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private object SetDeferred(bool value) { ShouldDefer = value; return null; }
            private object RequeueRepair() { State.RequeueRepair(); return null; }

            private object RecordRequeue(IEnumerable<int> ids)
            {
                MarkedBookIds.AddRange(ids);
                return null;
            }

            private bool Mark(int bookId)
            {
                MarkedBookIds.Add(bookId);
                return ShouldDefer;
            }
        }

        private class EventAggregatorProxy : DispatchProxy
        {
            public List<object> Published { get; } = new();
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name != nameof(NzbDrone.Core.Messaging.Events.IEventAggregator.PublishEvent)) throw new NotImplementedException(targetMethod?.Name);
                Published.Add(args[0]);
                return null;
            }
        }

        private sealed class MediaCoverProxyStub : IMediaCoverProxy
        {
            public int ProxyCalls { get; private set; }
            public int RegisterCalls { get; private set; }

            public string RegisterUrl(string url)
            {
                RegisterCalls++;
                return "/MediaCoverProxy/hash/cover.jpg";
            }

            public void ProxyRemoteUrls(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers)
            {
                ProxyCalls++;
                foreach (var cover in covers)
                {
                    cover.Url = RegisterUrl(cover.Url);
                }
            }

            public string GetUrl(string hash) => throw new NotImplementedException();
            public byte[] GetImageFromUrl(string url) => throw new NotImplementedException();
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();
            public List<Author> UpdatedAuthors { get; } = new();
            public Action BeforeGetExisting;
            public bool DetachedRemoval;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return Authors;
                }

                if (targetMethod?.Name == nameof(IAuthorService.GetExistingAuthors))
                {
                    BeforeGetExisting?.Invoke();
                    return Authors.Where(author => ((IEnumerable<int>)args[0]).Contains(author.Id)).ToList();
                }
                if (targetMethod?.Name == nameof(IAuthorService.RemoveKnownPlaceholderImages))
                {
                    var snapshot = (Author)args[0];
                    var urls = snapshot.Images.Where(image => image != null && MediaCoverRendition.IsKnownPlaceholderImageUrl(image.Url)).Select(image => image.Url).ToArray();
                    if (urls.Length == 0) return false;
                    if (!Authors.Any(a => a.Id == snapshot.Id)) Authors.Add(snapshot);
                    try { return Invoke(typeof(IAuthorService).GetMethod(nameof(IAuthorService.RemoveImageUrls)), new object[] { snapshot.Id, urls }); }
                    finally { snapshot.Images = snapshot.Images.Where(image => image == null || !urls.Contains(image.Url)).ToList(); }
                }

                if (targetMethod?.Name == nameof(IAuthorService.RemoveImageUrls))
                {
                    var author = Authors.Single(a => a.Id == (int)args[0]);
                    if (DetachedRemoval)
                    {
                        var stored = new Author { Id = author.Id, Name = author.Name, Images = author.Images };
                        Authors[Authors.IndexOf(author)] = stored;
                        author = stored;
                    }
                    author.Images = author.Images.Where(image => !((IReadOnlyCollection<string>)args[1]).Contains(image.Url)).ToList();
                    UpdatedAuthors.Add(author);
                    return true;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class CommandQueueProxy : DispatchProxy
        {
            public List<RepairAuthorMediaCoversCommand> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) &&
                    args?.FirstOrDefault() is RepairAuthorMediaCoversCommand command)
                {
                    Commands.Add(command);
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class CoverMapperStub : IMapCoversToLocal
        {
            public (bool Ready, byte[] Data) ReadLocalBookCover(Edition edition) => (false, null);

            public IDiskProvider DiskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            private MediaCoverService _reader;
            public void RepairBookCovers() { }
            public bool AuthorCoverMatchesRemoteUrl(int id, MediaCoverTypes type, string url, out bool unreadable)
            {
                var app = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
                ((AppFolderProxy)(object)app).AppDataFolder = ConfigRoot;
                _reader ??= new MediaCoverService(null, null, null, null, DiskProvider, app, null, null, null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
                return _reader.AuthorCoverMatchesRemoteUrl(id, type, url, out unreadable);
            }
            public void EnsureBookCoversForBooks(List<Book> books, bool forceDownload = false) => throw new NotImplementedException();
            public void EnsureBookCoversForImport(Book book) => EnsureBookCovers(book);
            public void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>> covers)
            {
                foreach (var book in covers)
                {
                    ConvertToLocalUrls(book.Key, MediaCoverEntity.Book, book.Value);
                }
            }

            public void DeleteBookCovers(int bookId) => throw new NotImplementedException();

            public bool Changed { get; set; } = true;
            public List<int> EnsuredAuthorIds { get; } = new();
            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null) => throw new NotImplementedException();

            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null)
            {
                var suffix = height.HasValue ? $"-{height.Value}" : string.Empty;
                return Path.Combine(ConfigRoot, "MediaCover", entityId.ToString(), $"poster{suffix}{extension}");
            }

            public bool EnsureAuthorCovers(Author author, bool forceDownload = false) { EnsuredAuthorIds.Add(author.Id); return Changed; }
            public void EnsureBookCovers(Book book, bool forceDownload = false) => throw new NotImplementedException();
            public Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover) => throw new NotImplementedException();
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public Action OnGetExistingBooks { get; set; }
            public List<int> RequestedAuthorIds { get; } = new();
            public List<int> EligibleLibraryBookIds { get; } = new();
            public List<List<int>> LibraryBookIdQueries { get; } = new();
            public Action OnLibraryBookIds;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetExistingBooks))
                {
                    OnGetExistingBooks?.Invoke();
                    var requestedIds = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Books.Where(book => requestedIds.Contains(book.Id)).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    RequestedAuthorIds.Add((int)args[0]);
                    return Books;
                }

                if (targetMethod?.Name == nameof(IBookService.GetLibraryBookIds))
                {
                    var requestedIds = (args?.FirstOrDefault() as IEnumerable<int>)?.Distinct().ToList() ?? new List<int>();
                    LibraryBookIdQueries.Add(requestedIds);
                    OnLibraryBookIds?.Invoke();
                    return requestedIds.Where(EligibleLibraryBookIds.Contains).ToList();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private static MediaCoverService CreateBookCoverSubject(
            CoverDownloadScope scope,
            IEnumerable<int> eligibleBookIds,
            out BookServiceProxy bookService,
            out DiskProviderProxy disk,
            out ImageHttpClientProxy httpClient,
            out DeferredCoverProxy deferred, MediaCoverProxyStub proxy = null, IDeferredCoverService deferredService = null, Action onReadScope = null, Logger logger = null)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var configFile = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var configService = DispatchProxy.Create<IConfigService, CoverScopeConfigProxy>();
            ((CoverScopeConfigProxy)(object)configService).Scope = scope;
            ((CoverScopeConfigProxy)(object)configService).OnRead = onReadScope;
            var bookServiceApi = DispatchProxy.Create<IBookService, BookServiceProxy>();
            bookService = (BookServiceProxy)(object)bookServiceApi;
            bookService.EligibleLibraryBookIds.AddRange(eligibleBookIds ?? Enumerable.Empty<int>());
            var diskApi = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            disk = (DiskProviderProxy)(object)diskApi;
            var httpApi = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            httpClient = (ImageHttpClientProxy)(object)httpApi;
            var deferredApi = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            deferred = (DeferredCoverProxy)(object)deferredApi;

            return new MediaCoverService(
                proxy ?? new MediaCoverProxyStub(),
                null,
                bookServiceApi,
                httpApi,
                diskApi,
                appFolder,
                configService,
                configFile,
                null,
                deferredService ?? deferredApi,
                logger ?? LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
        }

        [TestCase(CoverDownloadScope.AllBooks, false)]
        [TestCase(CoverDownloadScope.AllBooks, true)]
        [TestCase(CoverDownloadScope.LibraryBooks, false)]
        [TestCase(CoverDownloadScope.LibraryBooks, true)]
        public void overlapping_import_should_keep_covers_redeferred_by_the_real_cover_service(CoverDownloadScope scope, bool commandCompletion)
        {
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger(), new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var covers = CreateBookCoverSubject(scope, new[] { 1, 2 }, out var books, out _, out var http, out _, deferredService: deferred);
            books.Books = new List<Book>
            {
                CreateBookWithCover(1, true, "https://images.example/first.jpg"),
                CreateBookWithCover(2, true, "https://images.example/second.jpg")
            };
            var service = new DeferredCoverDownloadService(deferred, (IBookService)(object)books, null, covers, LogManager.GetCurrentClassLogger());
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "first start") { CommandId = 71 });
            deferred.MarkBooksForCoverDownload(new[] { 1, 2 });
            books.OnGetExistingBooks = () => service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "second start") { CommandId = 72 });

            if (commandCompletion)
            {
                service.Handle(new CommandExecutedEvent(new CommandModel { Id = 71 }));
            }
            else
            {
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "first done") { CommandId = 71 });
            }

            Assert.That(http.RequestedUrls, Is.Empty);
            Assert.That(deferred.GetPendingBookIds(), Is.EquivalentTo(new[] { 1, 2 }));
            books.OnGetExistingBooks = null;
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "second done") { CommandId = 72 });

            Assert.That(http.RequestedUrls, Is.EquivalentTo(new[] { "https://images.example/first.jpg", "https://images.example/second.jpg" }));
            Assert.That(deferred.GetPendingBookIds(), Is.Empty);
        }

        private static Book CreateBookWithCover(int id, bool monitoredEdition, string coverUrl, bool audiobookMonitored = false)
        {
            return new Book
            {
                Id = id,
                Title = $"Book {id}",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = audiobookMonitored,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = id * 10,
                        Monitored = monitoredEdition,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, coverUrl)
                        }
                    }
                }
            };
        }

        [Test]
        public void library_cover_batch_should_query_eligibility_once_and_skip_nonlibrary_books()
        {
            var subject = CreateBookCoverSubject(
                CoverDownloadScope.LibraryBooks,
                new[] { 2 },
                out var bookService,
                out _,
                out var httpClient,
                out var deferred);
            var books = new List<Book>
            {
                CreateBookWithCover(1, monitoredEdition: true, "https://images.example/unmonitored-one.jpg"),
                CreateBookWithCover(2, monitoredEdition: true, "https://images.example/monitored.jpg", audiobookMonitored: true),
                CreateBookWithCover(3, monitoredEdition: true, "https://images.example/unmonitored-two.jpg")
            };

            subject.EnsureBookCoversForBooks(books);

            Assert.That(bookService.LibraryBookIdQueries, Has.Count.EqualTo(1));
            Assert.That(bookService.LibraryBookIdQueries.Single(), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(httpClient.RequestedUrls, Is.EqualTo(new[] { "https://images.example/monitored.jpg" }));
            Assert.That(deferred.MarkedBookIds, Is.EqualTo(new[] { 2 }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void failed_batch_eligibility_is_read_once_and_retained_unless_cancelled(bool cancelled, bool failConfig)
        {
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger(), null);
            var failure = cancelled ? (Exception)new OperationCanceledException("Fixture cancelled") : new TimeoutException("Fixture eligibility timeout");
            var subject = CreateBookCoverSubject(CoverDownloadScope.LibraryBooks, new[] { 1, 2 },
                out var bookService, out _, out var http, out _, deferredService: deferred,
                onReadScope: failConfig ? () => throw failure : null);
            bookService.OnLibraryBookIds = () => throw failure;
            var books = new[] { 1, 2 }.Select(id => CreateBookWithCover(id, true, $"https://images.example/{id}.jpg")).ToList();
            Assert.That(Assert.Catch(() => subject.EnsureBookCoversForBooks(books)), Is.SameAs(failure));
            Assert.That(bookService.LibraryBookIdQueries, Has.Count.EqualTo(failConfig ? 0 : 1));
            Assert.That(http.RequestedUrls, Is.Empty);
            Assert.That(deferred.GetPendingBookIds(), Is.EqualTo(cancelled ? Array.Empty<int>() : new[] { 1, 2 }));
        }

        [TestCase("io")]
        [TestCase("access")]
        [TestCase("unexpected")]
        [TestCase("cancelled")]
        public void legacy_fingerprint_failure_does_not_queue_file_errors_or_hide_other_failures(string kind)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
            logs.Configuration = logging;
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger(), null);
            var subject = CreateBookCoverSubject(CoverDownloadScope.AllBooks, Array.Empty<int>(),
                out _, out var disk, out var http, out _, deferredService: deferred);
            var bad = CreateBookWithCover(1, true, "https://images.example/old.jpg");
            http.ResponseData = BuildPngBytes(24, 32, new Rgba32(20, 100, 200));
            subject.EnsureBookCovers(bad);
            var receipt = disk.TextByPath.Keys.Single();
            var json = Newtonsoft.Json.Linq.JObject.Parse(disk.TextByPath[receipt]);
            json["selectedEdition"]["contentHash"] = null;
            disk.TextByPath[receipt] = json.ToString();
            // Re-open the saved legacy receipt, then fail its image read outside the HTTP catch.
            subject = CreateBookCoverSubject(CoverDownloadScope.AllBooks, Array.Empty<int>(),
                out _, out var newDisk, out var newHttp, out _, deferredService: deferred, logger: logs.GetLogger("CoverFixture"));
            newHttp.ResponseData = http.ResponseData;
            foreach (var path in disk.ExistingPaths) newDisk.ExistingPaths.Add(path);
            foreach (var entry in disk.TextByPath) newDisk.TextByPath[entry.Key] = entry.Value;
            foreach (var entry in disk.BinaryByPath) newDisk.BinaryByPath[entry.Key] = entry.Value;
            bad.Editions.Single().Images.Clear();
            Exception failure = kind switch
            {
                "io" => new IOException("Fixture legacy image locked"),
                "access" => new UnauthorizedAccessException("Fixture legacy image denied"),
                "cancelled" => new OperationCanceledException("Fixture cancelled"),
                _ => new InvalidOperationException("Fixture unexpected cover failure")
            };
            newDisk.BeforeOpen = _ => throw failure;
            var cancelled = kind == "cancelled";
            var unreadable = kind is "io" or "access";
            var good = CreateBookWithCover(2, true, "https://images.example/good.jpg");
            if (cancelled) Assert.That(Assert.Catch(() => subject.EnsureBookCoversForBooks(new() { bad, good })), Is.SameAs(failure));
            else subject.EnsureBookCoversForBooks(new() { bad, good });
            Assert.That(newHttp.RequestedUrls, Is.EqualTo(cancelled ? Array.Empty<string>() : new[] { "https://images.example/good.jpg" }));
            Assert.That(deferred.GetPendingBookIds(), Is.EqualTo(kind == "unexpected" ? new[] { 1 } : Array.Empty<int>()));
            if (cancelled) return;
            if (unreadable)
            {
                for (var i = 0; i < 5; i++) subject.EnsureBookCoversForBooks(new() { bad });
                Assert.That(deferred.TakePendingBookIds(), Is.Empty, "Optional fingerprint reads must not schedule retries");
                Assert.That(messages.Logs.Where(line => line.StartsWith("Warn|")),
                    Is.EqualTo(new[] { $"Warn|Unable to read cover fingerprint for book 1 at {newDisk.BinaryByPath.Keys.First(path => Path.GetFileName(path) == "cover.jpg")}: {failure.Message}" }));
                Assert.That(newDisk.TextByPath[receipt], Is.EqualTo(json.ToString()), "An unknown fingerprint must not rewrite the receipt");
            }
            newDisk.BeforeOpen = null;
            var retry = deferred.TakePendingBookIds();
            subject.EnsureBookCoversForBooks(unreadable ? new() { bad } : new[] { bad, good }.Where(book => retry.Contains(book.Id)).ToList());
            Assert.That(newHttp.RequestedUrls, Has.Count.EqualTo(1), "Successful book is not retried");
            Assert.That(deferred.GetPendingBookIds(), Is.Empty);
            Assert.That((string)Newtonsoft.Json.Linq.JObject.Parse(newDisk.TextByPath[receipt])["selectedEdition"]["contentHash"],
                Is.EqualTo(MediaCoverRendition.ComputeContentSha256(http.ResponseData)), "A later successful read still upgrades the receipt");
        }

        [Test]
        public void all_books_cover_batch_should_not_query_library_eligibility()
        {
            var subject = CreateBookCoverSubject(
                CoverDownloadScope.AllBooks,
                Array.Empty<int>(),
                out var bookService,
                out _,
                out var httpClient,
                out _);
            var books = new List<Book>
            {
                CreateBookWithCover(11, monitoredEdition: true, "https://images.example/all-one.jpg"),
                CreateBookWithCover(12, monitoredEdition: true, "https://images.example/all-two.jpg")
            };

            subject.EnsureBookCoversForBooks(books);

            Assert.That(bookService.LibraryBookIdQueries, Is.Empty);
            Assert.That(httpClient.RequestedUrls, Is.EquivalentTo(new[]
            {
                "https://images.example/all-one.jpg",
                "https://images.example/all-two.jpg"
            }));
        }

        [Test]
        public void import_cover_should_bypass_scope_and_deferral_but_use_only_the_monitored_edition()
        {
            var subject = CreateBookCoverSubject(
                CoverDownloadScope.LibraryBooks,
                Array.Empty<int>(),
                out var bookService,
                out _,
                out var httpClient,
                out var deferred);
            deferred.ShouldDefer = true;
            var book = new Book
            {
                Id = 20,
                Title = "Imported book",
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 201,
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/unmonitored-edition.jpg")
                        }
                    },
                    new()
                    {
                        Id = 202,
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/monitored-edition.jpg")
                        }
                    }
                }
            };

            subject.EnsureBookCoversForImport(book);

            Assert.That(bookService.LibraryBookIdQueries, Is.Empty);
            Assert.That(deferred.MarkedBookIds, Is.Empty);
            Assert.That(httpClient.RequestedUrls, Is.EqualTo(new[] { "https://images.example/monitored-edition.jpg" }));
        }

        [Test]
        public void book_details_should_proxy_ineligible_cover_and_preserve_eligible_local_mapping()
        {
            var subject = CreateBookCoverSubject(
                CoverDownloadScope.LibraryBooks,
                new[] { 32 },
                out var bookService,
                out var disk,
                out var httpClient,
                out _);
            var eligibleUrl = "https://images.example/eligible.jpg";
            var metadataPath = Path.Combine(ConfigRoot, "MediaCover", "Books", "32", "cover-metadata.json");
            disk.ExistingPaths.Add(metadataPath);
            disk.TextByPath[metadataPath] = $"{{\"selectedEdition\":{{\"coverUrl\":\"{eligibleUrl}\",\"downloadedAt\":\"2026-08-07T01:02:03Z\"}}}}";
            var ineligibleCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/detail-only.jpg");
            var eligibleCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, eligibleUrl);

            subject.ConvertBookCoversToLocalUrls(new[]
            {
                new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(31, new List<NzbDrone.Core.MediaCover.MediaCover> { ineligibleCover }),
                new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(32, new List<NzbDrone.Core.MediaCover.MediaCover> { eligibleCover })
            });

            Assert.That(bookService.LibraryBookIdQueries, Has.Count.EqualTo(1));
            Assert.That(bookService.LibraryBookIdQueries.Single(), Is.EqualTo(new[] { 31, 32 }));
            Assert.That(ineligibleCover.Url, Is.Null);
            Assert.That(ineligibleCover.RemoteUrl, Is.EqualTo("https://images.example/detail-only.jpg"));
            Assert.That(ineligibleCover.TemporaryUrl, Does.StartWith("/MediaCoverProxy/book/31/detail-only.jpg?v="));
            Assert.That(eligibleCover.Url, Does.StartWith("/MediaCover/Books/32/cover.jpg?v="));
            Assert.That(eligibleCover.TemporaryUrl, Is.Null);
            Assert.That(httpClient.Calls, Is.Zero);

            var systemTextJson = System.Text.Json.JsonSerializer.Serialize(ineligibleCover);
            using var parsed = System.Text.Json.JsonDocument.Parse(systemTextJson);
            Assert.That(parsed.RootElement.GetProperty("TemporaryUrl").GetString(), Is.EqualTo(ineligibleCover.TemporaryUrl));
        }

        [TestCase(CoverDownloadScope.AllBooks)]
        [TestCase(CoverDownloadScope.LibraryBooks)]
        public void saved_book_mapping_should_not_register_urls_and_should_version_changed_art(CoverDownloadScope scope)
        {
            var proxy = new MediaCoverProxyStub();
            var subject = CreateBookCoverSubject(scope, Array.Empty<int>(), out var books, out _, out var http, out _, proxy);
            var covers = Enumerable.Range(1, 1000).Select(id => new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(
                id, new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, $"https://images.example/{id}.jpg") })).ToList();

            subject.ConvertBookCoversToLocalUrls(covers);
            var firstUrl = covers[0].Value[0].TemporaryUrl ?? covers[0].Value[0].Url;
            covers[0].Value[0].RemoteUrl = "https://images.example/new-edition.jpg";
            subject.ConvertBookCoversToLocalUrls(covers);
            var changedUrl = covers[0].Value[0].TemporaryUrl ?? covers[0].Value[0].Url;

            Assert.That(firstUrl, Does.StartWith("/MediaCoverProxy/book/1/1.jpg?v="));
            Assert.That(changedUrl, Does.StartWith("/MediaCoverProxy/book/1/new-edition.jpg?v="));
            Assert.That(changedUrl.Split("?v=")[1], Is.Not.EqualTo(firstUrl.Split("?v=")[1]));
            Assert.That(proxy.RegisterCalls, Is.Zero);
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.That(http.Calls, Is.Zero);
            Assert.That(books.LibraryBookIdQueries.Count, Is.EqualTo(scope == CoverDownloadScope.LibraryBooks ? 2 : 0));
        }

        [TestCase(".jpg")]
        [TestCase(".jpeg")]
        [TestCase(".png")]
        [TestCase(".webp")]
        public void resized_author_cover_should_be_mapped_locally_when_original_was_deleted(string extension)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var expectedPath = Path.Combine(ConfigRoot, "MediaCover", "12", $"poster-250{extension}");
            disk.ExistingPaths.Add(expectedPath);
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(
                $"https://images.example/author{extension}",
                VerifiedRealImageHash);
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, $"https://images.example/author{extension}");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var getFileSizeCalls = disk.GetCallCount(nameof(IDiskProvider.GetFileSize));
            var readAllTextCalls = disk.GetCallCount(nameof(IDiskProvider.ReadAllText));
            var fileGetLastWriteCalls = disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, $"https://images.example/author{extension}");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { secondCover });

            Assert.That(cover.Url, Is.EqualTo($"/MediaCover/12/poster-250{extension}?v={VerifiedRealImageHash}"));
            Assert.That(cover.RemoteUrl, Is.EqualTo($"https://images.example/author{extension}"));
            Assert.That(secondCover.Url, Is.EqualTo(cover.Url));
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.GetFileSize)), Is.EqualTo(getFileSizeCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.EqualTo(readAllTextCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.EqualTo(fileGetLastWriteCalls));
            });
        }

        [Test]
        public void author_cover_without_remote_identity_should_not_relabel_stale_local_bytes()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-250.jpg"));
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/real-author.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            Assert.That(cover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(cover.RemoteUrl, Is.EqualTo("https://images.example/real-author.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
        }

        [Test]
        public void legacy_author_cover_without_verified_content_identity_should_not_be_relabelled()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-250.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-500.png"));
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: null,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = 12,
                Name = "Legacy Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/legacy.png")
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(disk.ExistingPaths, Does.Not.Contain(Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url")));
        }

        [Test]
        public void selected_author_variant_should_map_locally_without_probing_disk_and_leave_other_candidates_remote()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var remoteUrl = "https://images.example/alternate-author.jpg";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(remoteUrl))).ToLowerInvariant()[..16];
            var selectedHash = MediaCoverRendition.ComputeStableAuthorImageHash(remoteUrl, MediaCoverTypes.Poster);
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var selectedCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, remoteUrl);
            var otherCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/other-author.jpg");

            var covers = new[] { otherCover, selectedCover };
            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers, selectedHash);

            Assert.That(covers, Is.EqualTo(new[] { otherCover, selectedCover }));
            Assert.That(selectedCover.Url, Is.EqualTo($"/MediaCover/12/poster-{hash}.jpg"));
            Assert.That(selectedCover.RemoteUrl, Is.EqualTo(remoteUrl));
            Assert.That(otherCover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(otherCover.RemoteUrl, Is.EqualTo("https://images.example/other-author.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
            Assert.That(disk.CallCounts, Is.Empty);
        }

        [TestCase(MediaCoverTypes.Poster)]
        [TestCase(MediaCoverTypes.Banner)]
        [TestCase(MediaCoverTypes.Fanart)]
        public void canonical_author_fallback_should_keep_provider_order_in_serialized_list(MediaCoverTypes coverType)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", $"{coverType.ToString().ToLowerInvariant()}.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(
                "https://images.example/fallback.jpg", VerifiedRealImageHash);
            var subject = new MediaCoverService(new MediaCoverProxyStub(), null, null, null,
                diskProvider, appFolder, null, config, null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var failed = new NzbDrone.Core.MediaCover.MediaCover(coverType, "https://images.example/failed.jpg");
            var fallback = new NzbDrone.Core.MediaCover.MediaCover(coverType, "https://images.example/fallback.jpg");
            var alternate = new NzbDrone.Core.MediaCover.MediaCover(coverType, "https://images.example/alternate.jpg");
            var covers = new List<NzbDrone.Core.MediaCover.MediaCover> { failed, fallback, alternate };

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);

            Assert.That(fallback.Url, Does.StartWith($"/MediaCover/12/{coverType.ToString().ToLowerInvariant()}-"));
            Assert.That(new[] { failed, alternate }.All(cover => cover.Url.StartsWith("/MediaCoverProxy/")), Is.True);
            var calls = disk.CallCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);
            Assert.That(disk.CallCounts, Is.EquivalentTo(calls));

            var author = new Author
            {
                Id = 12,
                Name = "Example Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(coverType, failed.RemoteUrl),
                    new(coverType, fallback.RemoteUrl),
                    new(coverType, alternate.RemoteUrl)
                }
            };
            var resource = author.ToResource();
            subject.ConvertToLocalUrls(resource.Id, MediaCoverEntity.Author, resource.Images);
            var json = STJson.ToJson(resource);
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            Assert.That(author.Images[0].Url, Is.EqualTo(failed.RemoteUrl), "API ordering must not mutate stored/provider order");
            if (coverType == MediaCoverTypes.Poster)
            {
                var responsePath = Environment.GetEnvironmentVariable("CHAPTARR_PHOTO_RESPONSE") ??
                    Path.Combine(TestContext.CurrentContext.WorkDirectory, "author-photo-response.json");
                File.WriteAllText(responsePath, json);
                TestContext.AddTestAttachment(responsePath, "Serialized author API input for the browser regression fixture");

                var selections = author.Images.Select(image =>
                {
                    author.SelectedPosterHash = MediaCoverRendition.ComputeStableAuthorImageHash(image.Url, coverType);
                    var selectedResource = author.ToResource();
                    subject.ConvertToLocalUrls(author.Id, MediaCoverEntity.Author, selectedResource.Images, author.SelectedPosterHash);
                    return selectedResource;
                }).ToArray();
                File.WriteAllText(responsePath + ".selections.json", STJson.ToJson(selections));
                TestContext.AddTestAttachment(responsePath + ".selections.json", "Author updates after each selected photo");
                foreach (var selection in selections)
                {
                    Assert.That(selection.Images.Select(image => image.RemoteUrl), Is.EqualTo(author.Images.Select(image => image.Url)));
                }
            }

            Assert.That(covers, Is.EqualTo(new[] { failed, fallback, alternate }));
            Assert.That(parsed.RootElement.GetProperty("images")[1].GetProperty("url").GetString(), Is.EqualTo(fallback.Url));
        }

        [TestCase(MediaCoverEntity.Author, 0)]
        [TestCase(MediaCoverEntity.Author, 12)]
        [TestCase(MediaCoverEntity.Book, 12)]
        public void photo_order_should_stay_unchanged_without_a_local_author_choice(MediaCoverEntity entity, int id)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var metadataPath = Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json");
            disk.ExistingPaths.Add(metadataPath);
            disk.TextByPath[metadataPath] = "{\"selectedEdition\":{\"coverUrl\":\"https://images.example/second.jpg\",\"downloadedAt\":\"2026-08-07T01:02:03Z\"}}";
            var subject = new MediaCoverService(new MediaCoverProxyStub(), null, null, null,
                diskProvider, appFolder, null, config, null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var type = entity == MediaCoverEntity.Book ? MediaCoverTypes.Cover : MediaCoverTypes.Poster;
            var first = new NzbDrone.Core.MediaCover.MediaCover(type, "https://images.example/first.jpg");
            var second = new NzbDrone.Core.MediaCover.MediaCover(type, "https://images.example/second.jpg");
            var covers = new[] { first, second };

            subject.ConvertToLocalUrls(id, entity, covers);

            Assert.That(covers, Is.EqualTo(new[] { first, second }));
            if (entity == MediaCoverEntity.Book)
            {
                Assert.That(second.Url, Does.StartWith("/MediaCover/Books/12/cover.jpg?v="));
            }

            subject.ConvertToLocalUrls(id, entity, Array.Empty<NzbDrone.Core.MediaCover.MediaCover>());
            subject.ConvertToLocalUrls(id, entity, null);
        }

        [Test]
        public void canonical_author_cover_should_use_the_first_real_image_per_cover_type()
        {
            const string rejectedByContent = "https://assets.hardcover.app/author/900001/provider-default.jpg";
            Assert.That(MediaCoverRendition.RegisterKnownPlaceholderImage(rejectedByContent, KnownPlaceholderJpegHash), Is.True);
            var covers = new[]
            {
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/nophoto.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, rejectedByContent + "?size=500"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/primary.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/alternate.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Banner, "https://images.example/banner.jpg")
            };

            var selected = MediaCoverRendition.SelectCanonicalCovers(covers);

            Assert.That(selected.Select(cover => cover.Url), Is.EqualTo(new[]
            {
                "https://images.example/primary.jpg",
                "https://images.example/banner.jpg"
            }));
        }

        [TestCase("47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e")]
        [TestCase("db25714c302dcc8ccca766d734947df2931fcc74cbed1656ad2eb470613db981")]
        [TestCase("8280bac30e108aa599176cc0737e1179e8225fe2b08d98187a6ebcb22b126a6e")]
        [TestCase("38eb593837bb848a936fae31d959d4795f1b846b3cd57d956483d721dda39478")]
        public void all_live_hardcover_mascot_encodings_should_be_known(string contentHash)
        {
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash), Is.True);
        }

        [Test]
        public void amazon_shared_default_author_image_should_be_rejected_by_url_and_content()
        {
            const string url = "https://m.media-amazon.com/images/I/01Kv-W2ysOL.png";
            const string contentHash = "a5efe6ec77a9e993915eece1864c4fae3e49e13773f573aa99eb950fd4089b60";

            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(url), Is.True);
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(url + "?size=500#author"), Is.True);
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash), Is.True);
            Assert.That(MediaCoverRendition.SelectCandidates(new[]
            {
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, url)
            }), Is.Empty);
        }

        [Test]
        public void downloaded_mascot_bytes_should_register_every_url_for_the_shared_policy()
        {
            const string misleadingJpegUrl = "https://assets.hardcover.app/author/900003/reencoded.jpg?width=270";
            var mascotBytes = Convert.FromBase64String(MascotWebpBase64);

            var rejected = MediaCoverRendition.InspectDownloadedImage(misleadingJpegUrl, mascotBytes, out var contentHash);

            Assert.That(rejected, Is.True);
            Assert.That(contentHash, Is.EqualTo(KnownPlaceholderWebpHash));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl("https://assets.hardcover.app/author/900003/reencoded.jpg#alternate"), Is.True);
        }

        [Test]
        public void author_cover_identity_should_require_a_post_download_content_verdict()
        {
            const string url = "https://images.example/verified-author.jpg";
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = url;

            var reader = new CoverMapperStub { DiskProvider = diskProvider };
            Assert.That(reader.AuthorCoverMatchesRemoteUrl(12, MediaCoverTypes.Poster, url, out _), Is.False,
                "Legacy URL-only sidecars have not proved the bytes and must be revalidated once.");

            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(url, VerifiedRealImageHash);

            reader = new CoverMapperStub { DiskProvider = diskProvider };
            Assert.That(reader.AuthorCoverMatchesRemoteUrl(12, MediaCoverTypes.Poster, url, out _), Is.True);
        }

        [Test]
        public async Task on_demand_author_image_should_reject_known_provider_placeholder()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            ((ImageHttpClientProxy)(object)httpClient).ResponseData = Convert.FromBase64String(MascotWebpBase64);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));

            var placeholderUrl = "https://assets.hardcover.app/author/900002/extension-lies.jpg";
            var author = new Author
            {
                Id = 900002,
                Name = "No Photo Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholderUrl)
                }
            };

            ((AuthorServiceProxy)(object)authorService).Authors.Add(author);
            var result = await subject.EnsureAuthorImage(
                author,
                author.Images.Single());

            Assert.That(result.State, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("placeholder_image"));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(placeholderUrl), Is.True);
            Assert.That(author.Images, Is.Empty);
            Assert.That(((AuthorServiceProxy)(object)authorService).UpdatedAuthors, Has.Count.EqualTo(1));
            Assert.That(((DiskProviderProxy)(object)diskProvider).BinaryByPath, Is.Empty);
        }

        [Test]
        public async Task legacy_on_demand_mascot_file_should_be_rejected_without_a_network_request()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            const string placeholderUrl = "https://assets.hardcover.app/author/900006/legacy-default.jpg";
            var urlHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(placeholderUrl))).ToLowerInvariant()[..16];
            var variantPath = Path.Combine(ConfigRoot, "MediaCover", "900006", $"poster-{urlHash}.jpg");
            disk.ExistingPaths.Add(variantPath);
            disk.BinaryByPath[variantPath] = Convert.FromBase64String(MascotWebpBase64);
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = 900006,
                Name = "Legacy Placeholder Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholderUrl)
                }
            };

            var result = await subject.EnsureAuthorImage(author, author.Images.Single());

            Assert.That(result.ErrorCode, Is.EqualTo("placeholder_image"));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(variantPath));
            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.Zero);
            Assert.That(author.Images, Is.Empty);
        }

        [Test]
        public void canonical_download_should_reject_mascot_and_persist_the_real_fallback_identity()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var responses = (ImageHttpClientProxy)(object)httpClient;
            var mascotBytes = Convert.FromBase64String(MascotWebpBase64);
            var realImageBytes = BuildPngBytes(32, 48, new Rgba32(30, 120, 210));
            responses.ResponseQueue.Enqueue(mascotBytes); // full download
            responses.ResponseQueue.Enqueue(realImageBytes); // full download
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            const string mascotUrl = "https://assets.hardcover.app/author/900007/provider-default.jpg";
            const string realUrl = "https://assets.hardcover.app/author/900007/real-fallback.jpg";
            var author = new Author
            {
                Id = 900007,
                Name = "Fallback Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, mascotUrl),
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            ((AuthorServiceProxy)(object)authorService).Authors.Add(author);
            subject.EnsureAuthorCovers(author);

            var imagePath = Path.Combine(ConfigRoot, "MediaCover", "900007", "poster.jpg");
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "900007", "poster.url");
            Assert.That(author.Images.Select(image => image.Url), Is.EqualTo(new[] { realUrl }));
            Assert.That(disk.BinaryByPath[imagePath], Is.EqualTo(realImageBytes));
            Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(disk.TextByPath[identityPath], out var storedUrl, out var storedHash), Is.True);
            Assert.That(storedUrl, Is.EqualTo(realUrl));
            Assert.That(storedHash, Is.EqualTo(MediaCoverRendition.ComputeContentSha256(realImageBytes)));
            Assert.That(responses.Calls, Is.EqualTo(2));
            Assert.That(((AuthorServiceProxy)(object)authorService).UpdatedAuthors, Has.Count.EqualTo(1));
        }

        [Test]
        public void rejected_preferred_photo_should_not_delete_a_verified_real_fallback()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string mascotUrl = "https://assets.hardcover.app/author/900009/provider-default.jpg";
            const string realUrl = "https://assets.hardcover.app/author/900009/real-fallback.jpg";
            disk.ExistingPaths.UnionWith(new[]
            {
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-250.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-500.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")
            });
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(realUrl, VerifiedRealImageHash);
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            ((ImageHttpClientProxy)(object)httpClient).ResponseData = Convert.FromBase64String(MascotWebpBase64);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = 900009,
                Name = "Fallback Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, mascotUrl),
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(author.Images.Select(image => image.Url), Is.EqualTo(new[] { realUrl }));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-250.jpg")));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-500.jpg")));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")));
            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task verified_local_author_image_does_not_need_working_DNS(bool cached)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string url = "https://unresolvable-cover.invalid/author.png";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()[..16];
            var path = Path.Combine(ConfigRoot, "MediaCover", "900008", $"poster-{hash}.png");
            if (cached)
            {
                disk.ExistingPaths.UnionWith(new[] { path, path + ".sha256" });
                disk.BinaryByPath[path] = BuildPngBytes(32, 48, new Rgba32(20, 120, 210));
                disk.TextByPath[path + ".sha256"] = MediaCoverRendition.OriginalImageVerificationPrefix + MediaCoverRendition.ComputeContentSha256(disk.BinaryByPath[path]);
            }

            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var http = (ImageHttpClientProxy)(object)httpClient;
            http.Failure = new WebException("Couldn't resolve the image host.", WebExceptionStatus.NameResolutionFailure);
            var subject = new MediaCoverService(null, null, null, httpClient, diskProvider, appFolder, null, null, null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author { Id = 900008, Name = "Cached author" };
            var result = await subject.EnsureAuthorImage(author, new(MediaCoverTypes.Poster, url));
            Assert.That(result.State, Is.EqualTo(cached ? "downloaded" : "error"));
            Assert.That(http.Calls, Is.EqualTo(cached ? 0 : 1));
            if (!cached)
            {
                Assert.That(result.ErrorCode, Is.EqualTo("download_failed"));
            }
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public async Task on_demand_unsafe_target_is_Debug_but_network_failure_is_Warn(bool unsafeTarget, bool cached)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
            logs.Configuration = logging;
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var http = (ImageHttpClientProxy)(object)httpClient;
            const string url = "http://private-cover.invalid/author.png";
            var cache = (DiskProviderProxy)(object)disk;
            var folder = Path.Combine(ConfigRoot, "MediaCover", "900008");
            var cachedPath = Path.Combine(folder, "poster-500.png");
            var identity = Path.Combine(folder, "poster.url");
            var bytes = BuildPngBytes(64, 96, new Rgba32(20, 120, 210));
            if (cached)
            {
                cache.ExistingPaths.UnionWith(new[] { cachedPath, identity });
                cache.BinaryByPath[cachedPath] = bytes;
                cache.TextByPath[identity] = MediaCoverRendition.BuildAuthorCoverIdentity(url, VerifiedRealImageHash);
            }

            http.Failure = unsafeTarget
                ? Assert.Throws<UnsafeImageUrlException>(() => MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null)
                    .ValidateAddress(new Uri(url), IPAddress.Any))
                : new WebException("Couldn't resolve the image host.", WebExceptionStatus.NameResolutionFailure);
            var subject = new MediaCoverService(null, null, null, httpClient, disk, appFolder, null, null, null, null, logs.GetLogger("CoverFixture"), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var result = await subject.EnsureAuthorImage(new Author { Id = 900008, Name = "Test author" }, new(MediaCoverTypes.Poster, url));
            Assert.That(http.Calls, Is.EqualTo(1));
            Assert.That(result.State, Is.EqualTo(cached ? "downloaded" : "error"));
            if (cached)
            {
                Assert.That(result.ErrorCode, Is.Null);
                Assert.That(cache.BinaryByPath[result.Path], Is.EqualTo(bytes));
                Assert.That(cache.BinaryByPath[cachedPath], Is.EqualTo(bytes));
                Assert.That(cache.TextByPath[result.Path + ".sha256"], Is.EqualTo("cached-rendition"));
                Assert.That(cache.ExistingPaths, Does.Not.Contain(Path.Combine(folder, "poster-250.png")));
            }
            else
            {
                Assert.That(result.ErrorCode, Is.EqualTo(unsafeTarget ? "unsafe_url" : "download_failed"));
            }
            Assert.That(messages.Logs.Count(x => x.StartsWith("Warn|")), Is.EqualTo(unsafeTarget ? 0 : 1));
            if (unsafeTarget)
            {
                Assert.That(messages.Logs, Does.Contain("Debug|Blocked unsafe image URL 'http://private-cover.invalid/author.png': private_or_local_address"));
            }
        }

        [Test]
        public void blocked_preferred_photo_keeps_stored_original_for_resize_instead_of_fetching_another_provider()
        {
            using var logFactory = new LogFactory();
            var logs = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, logs);
            logFactory.Configuration = logging;
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string preferred = "https://preferred-cover.invalid/photo.jpg";
            const string fallback = "https://fallback-cover.invalid/photo.jpg";
            var original = Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.jpg");
            var identity = Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.url");
            var bytes = BuildPngBytes(32, 48, new Rgba32(20, 120, 210));
            var storedIdentity = MediaCoverRendition.BuildAuthorCoverIdentity(preferred, MediaCoverRendition.ComputeContentSha256(bytes));
            disk.ExistingPaths.UnionWith(new[] { original, identity });
            disk.BinaryByPath[original] = bytes;
            disk.TextByPath[identity] = storedIdentity;
            var requested = new List<string>();
            var http = InterfaceProxy.Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                requested.Add(request.Url.FullUri);
                if (request.Url.FullUri == preferred)
                {
                    request.TargetPolicy.ValidateAddress((Uri)request.Url, IPAddress.Any);
                }

                return new HttpResponse(request, new HttpHeader(), bytes);
            });
            var resized = new List<int>();
            var sourcePaths = new List<string>();
            var sourceBytes = new List<byte[]>();
            var resizer = InterfaceProxy.Proxy<IImageResizer>((_, args) =>
            {
                sourcePaths.Add((string)args[0]);
                sourceBytes.Add(disk.BinaryByPath[(string)args[0]].ToArray());
                resized.Add((int)args[2]);
                disk.ExistingPaths.Add((string)args[1]);
                disk.BinaryByPath[(string)args[1]] = bytes;
                return null;
            });
            var subject = new MediaCoverService(null, resizer, null, http, diskProvider, appFolder, null, null, null, null, logFactory.GetLogger("StoredCoverFixture"), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            subject.EnsureAuthorCovers(new Author { Id = 900008, Name = "Stored photo", Images = new()
            {
                new(MediaCoverTypes.Poster, preferred), new(MediaCoverTypes.Poster, fallback)
            } });

            Assert.That(requested, Is.Empty, "The verified original repairs sizes without contacting its blocked host");
            Assert.That(sourcePaths, Is.All.EqualTo(original));
            foreach (var source in sourceBytes) Assert.That(source, Is.EqualTo(bytes));
            Assert.That(resized, Is.EqualTo(MediaCoverRendition.GetHeights(MediaCoverTypes.Poster)));
            Assert.That(disk.TextByPath[identity], Is.EqualTo(storedIdentity));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(original), "Normal successful resizing removes the original afterward");
            foreach (var height in resized)
            {
                Assert.That(disk.BinaryByPath[Path.Combine(Path.GetDirectoryName(original), $"poster-{height}.jpg")], Is.EqualTo(bytes));
            }
            Assert.That(logs.Logs.Where(line => line.StartsWith("Warn|") || line.StartsWith("Error|")), Is.Empty);
        }

        [TestCase("same", 1, "timeout", true, 250)]
        [TestCase("same", 1, "http", true, 250)]
        [TestCase("same", 1, "unsafe", true, 250)]
        [TestCase("different", 1, "http", false, 250)]
        [TestCase("missing", 1, "http", false, 250)]
        [TestCase("same", 0, "http", false, 250)]
        [TestCase("same", -1, "http", false, 250)]
        [TestCase("same", 1, "unsafe", true, 500)]
        [TestCase("same", 1, "http", true, 500)]
        public void author_refresh_preserves_only_usable_same_selection_renditions_and_retries_missing_sizes(
            string identityKind, int cachedSize, string failure, bool retain, int cachedHeight)
        {
            using var logFactory = new LogFactory();
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string preferred = "https://preferred-cover.invalid/retained.jpg";
            const string fallback = "https://fallback-cover.invalid/replacement.jpg";
            var folder = Path.Combine(ConfigRoot, "MediaCover", "900012");
            var original = Path.Combine(folder, "poster.jpg");
            var cached = Path.Combine(folder, $"poster-{cachedHeight}.jpg");
            var identity = Path.Combine(folder, "poster.url");
            var oldBytes = BuildPngBytes(16, 24, new Rgba32(20, 120, 210));
            var newBytes = BuildPngBytes(32, 48, new Rgba32(210, 120, 20));
            var storedIdentity = MediaCoverRendition.BuildAuthorCoverIdentity(
                identityKind == "different" ? "https://other-cover.invalid/photo.jpg" : preferred,
                MediaCoverRendition.ComputeContentSha256(oldBytes));
            if (identityKind != "missing")
            {
                disk.ExistingPaths.Add(identity);
                disk.TextByPath[identity] = storedIdentity;
            }

            if (cachedSize >= 0)
            {
                disk.ExistingPaths.Add(cached);
                disk.BinaryByPath[cached] = cachedSize > 0 ? oldBytes : Array.Empty<byte>();
            }

            var unavailable = true;
            var requested = new List<string>();
            var http = InterfaceProxy.Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                requested.Add(request.Url.FullUri);
                if (unavailable && request.Url.FullUri == preferred)
                {
                    if (failure == "unsafe")
                    {
                        request.TargetPolicy.ValidateAddress((Uri)request.Url, IPAddress.Any);
                    }

                    if (failure == "timeout")
                    {
                        throw new WebException("Image request timed out.", WebExceptionStatus.Timeout);
                    }

                    throw new HttpException(new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), HttpStatusCode.ServiceUnavailable));
                }

                return new HttpResponse(request, new HttpHeader(), newBytes);
            });
            var resized = new List<int>();
            var resizeSources = new List<string>();
            var resizer = InterfaceProxy.Proxy<IImageResizer>((_, args) =>
            {
                resizeSources.Add((string)args[0]);
                resized.Add((int)args[2]);
                disk.ExistingPaths.Add((string)args[1]);
                disk.BinaryByPath[(string)args[1]] = disk.BinaryByPath[(string)args[0]];
                return null;
            });
            var health = new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var subject = new MediaCoverService(null, resizer, null, http, diskProvider, appFolder, null, null, null, null, logFactory.GetLogger("StoredRenditionFixture"), imageHostHealth: health);
            var author = new Author { Id = 900012, Name = "Stored rendition", Images = new()
            {
                new(MediaCoverTypes.Poster, preferred), new(MediaCoverTypes.Poster, fallback)
            } };
            subject.EnsureAuthorCovers(author);

            if (!retain)
            {
                Assert.That(requested, Does.Contain(fallback), "An unrelated, empty or absent photo must not prevent provider fallback");
                Assert.That(disk.BinaryByPath[cached], Is.EqualTo(newBytes));
                AssertAuthorReceipt(disk.TextByPath[identity], fallback, MediaCoverRendition.ComputeContentSha256(newBytes));
                return;
            }

            Assert.That(requested, Is.EqualTo(new[] { preferred }));
            Assert.That(resized, Is.Empty, "Do not upscale the surviving small photo into a replacement original");
            Assert.That(disk.BinaryByPath[cached], Is.EqualTo(oldBytes));
            Assert.That(disk.TextByPath[identity], Is.EqualTo(storedIdentity));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(Path.Combine(folder, $"poster-{(cachedHeight == 250 ? 500 : 250)}.jpg")));
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.SaveStream)), Is.Zero);
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.DeleteFile)), Is.Zero);

            unavailable = false;
            requested.Clear();
            if (failure != "unsafe")
            {
                subject.EnsureAuthorCovers(author);
                Assert.That(requested, Is.Empty, "Recovery must wait for the shared host pause");
                health.GetStatus("image:https://preferred-cover.invalid").RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            }
            subject.EnsureAuthorCovers(author);
            Assert.That(requested, Is.EqualTo(new[] { preferred }));
            Assert.That(resized, Is.EqualTo(MediaCoverRendition.GetHeights(MediaCoverTypes.Poster)));
            Assert.That(resizeSources, Is.All.EqualTo(original));
            Assert.That(disk.BinaryByPath[cached], Is.EqualTo(newBytes));
            AssertAuthorReceipt(disk.TextByPath[identity], preferred, MediaCoverRendition.ComputeContentSha256(newBytes));

            requested.Clear();
            subject.EnsureAuthorCovers(author);
            Assert.That(requested, Is.Empty, "A fully restored photo must return to the no-network path");
        }

        private static void AssertAuthorReceipt(string receipt, string expectedUrl, string expectedHash)
        {
            Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(receipt, out var url, out var hash, out var version), Is.True);
            Assert.That(url, Is.EqualTo(expectedUrl));
            Assert.That(hash, Is.EqualTo(expectedHash));
            Assert.That(version, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(hash));
        }

        [Test]
        public void verified_author_renditions_should_not_be_downloaded_again()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string realUrl = "https://images.example/persisted-author.jpg";
            disk.ExistingPaths.UnionWith(new[]
            {
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster-250.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster-500.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.url")
            });
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(realUrl, VerifiedRealImageHash);
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = 900008,
                Name = "Persisted Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.Zero);
        }

        [TestCase(false, true, ".jpg", ".jpg", true, false)]
        [TestCase(false, false, ".jpg", ".jpg", true, false)]
        [TestCase(true, true, ".jpg", ".jpg", true, false)]
        [TestCase(false, true, ".jfif", ".jpg", true, false)]
        [TestCase(false, true, ".png", ".jpg", true, false)]
        [TestCase(false, true, ".png", ".png", true, false)]
        [TestCase(false, true, ".jpg", ".jpg", false, false)]
        [TestCase(true, true, ".jpg", ".jpg", false, false)]
        [TestCase(false, true, ".jpg", ".jpg", true, true)]
        [TestCase(false, true, ".jpg", ".jpg", false, true)]
        public async Task on_demand_author_image_uses_renditions_only_after_failure_and_retries_the_original(
            bool hasOriginal, bool has500, string sourceExtension, string cachedExtension, bool providerFails, bool legacyVariant)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var proxy = new MediaCoverProxyStub();
            var remoteUrl = "https://assets.hardcover.app/authors/166157/6390041-L" + sourceExtension;
            const int authorId = 900010;
            var authorFolder = Path.Combine(ConfigRoot, "MediaCover", authorId.ToString());
            var identityPath = Path.Combine(authorFolder, "poster.url");
            var originalPath = Path.Combine(authorFolder, "poster" + sourceExtension);
            var cached250Path = Path.Combine(authorFolder, "poster-250" + cachedExtension);
            var cached500Path = Path.Combine(authorFolder, "poster-500" + cachedExtension);
            var originalBytes = BuildPngBytes(80, 120, new Rgba32(20, 120, 210));
            var cached250Bytes = BuildPngBytes(32, 48, new Rgba32(20, 120, 210));
            var cached500Bytes = BuildPngBytes(64, 96, new Rgba32(20, 120, 210));
            var expectedBytes = !providerFails || hasOriginal ? originalBytes : has500 ? cached500Bytes : cached250Bytes;
            var responses = (ImageHttpClientProxy)(object)httpClient;
            responses.ResponseData = originalBytes;
            responses.FailureStatus = providerFails ? HttpStatusCode.Forbidden : null;
            var urlHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(remoteUrl))).ToLowerInvariant()[..16];
            var variantPath = Path.Combine(authorFolder, $"poster-{urlHash}{sourceExtension}");
            var verificationPath = variantPath + ".sha256";
            disk.ExistingPaths.UnionWith(new[] { identityPath, cached250Path });
            if (hasOriginal)
            {
                disk.ExistingPaths.Add(originalPath);
                disk.BinaryByPath[originalPath] = originalBytes;
            }
            if (has500)
            {
                disk.ExistingPaths.Add(cached500Path);
                disk.BinaryByPath[cached500Path] = cached500Bytes;
            }
            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(remoteUrl, VerifiedRealImageHash);
            disk.BinaryByPath[cached250Path] = cached250Bytes;
            var subject = new MediaCoverService(
                mediaCoverProxy: proxy,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: config,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = authorId,
                Name = "Philip Pullman",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, remoteUrl)
                }
            };

            if (legacyVariant)
            {
                disk.ExistingPaths.UnionWith(new[] { variantPath, verificationPath });
                disk.BinaryByPath[variantPath] = cached500Bytes;
                disk.TextByPath[verificationPath] = MediaCoverRendition.ComputeContentSha256(cached500Bytes);
            }

            var result = await subject.EnsureAuthorImage(author, author.Images.Single());
            var hasRestoredVariant = disk.BinaryByPath.TryGetValue(variantPath, out var restoredBytes);
            var hasVerificationHash = disk.TextByPath.TryGetValue(verificationPath, out var verificationHash);
            var httpCallsAfterRestore = ((ImageHttpClientProxy)(object)httpClient).Calls;
            var savesAfterRestore = disk.GetCallCount(nameof(IDiskProvider.SaveStream));
            var metadataWritesAfterRestore = disk.GetCallCount(nameof(IDiskProvider.WriteAllText));
            var timestampWritesAfterRestore = disk.GetCallCount(nameof(IDiskProvider.FileSetLastWriteTime));
            var cachedResult = await subject.EnsureAuthorImage(author, author.Images.Single());
            var httpCallsAfterCacheHit = ((ImageHttpClientProxy)(object)httpClient).Calls;

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo("downloaded"));
                Assert.That(result.Path, Is.EqualTo(variantPath));
                Assert.That(hasRestoredVariant, Is.True);
                if (hasRestoredVariant)
                {
                    Assert.That(restoredBytes, Is.EqualTo(expectedBytes));
                }
                Assert.That(hasVerificationHash, Is.True);
                if (hasVerificationHash)
                {
                    var expectedVerification = providerFails
                        ? legacyVariant ? MediaCoverRendition.ComputeContentSha256(cached500Bytes) : "cached-rendition"
                        : MediaCoverRendition.OriginalImageVerificationPrefix + MediaCoverRendition.ComputeContentSha256(expectedBytes);
                    Assert.That(verificationHash, Is.EqualTo(expectedVerification));
                }
                Assert.That(MediaCoverRendition.StoredContentHashIsVerified(verificationPath, diskProvider), Is.EqualTo(!providerFails));
                Assert.That(httpCallsAfterRestore, Is.EqualTo(1));
                Assert.That(cachedResult.State, Is.EqualTo("downloaded"));
                Assert.That(cachedResult.Path, Is.EqualTo(variantPath));
                Assert.That(httpCallsAfterCacheHit, Is.EqualTo(providerFails ? 2 : 1));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.SaveStream)), Is.EqualTo(savesAfterRestore), "Serving the fallback again must not rewrite its bytes");
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.WriteAllText)), Is.EqualTo(metadataWritesAfterRestore), "The unchanged verification sidecar must not be rewritten");
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileSetLastWriteTime)), Is.EqualTo(timestampWritesAfterRestore));
            });

            // A fallback survives a restart and a canonical-photo change, but is never promoted.
            subject = new MediaCoverService(proxy, null, null, httpClient, diskProvider, appFolder, null, config, null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            disk.ExistingPaths.ExceptWith(new[] { originalPath, cached250Path, cached500Path, identityPath });
            if (providerFails)
            {
                var offline = await subject.EnsureAuthorImage(author, author.Images.Single());
                Assert.That(offline.State, Is.EqualTo("downloaded"));
                Assert.That(MediaCoverRendition.StoredContentHashIsVerified(verificationPath, diskProvider), Is.False);
            }

            responses.FailureStatus = null;
            var recovered = await subject.EnsureAuthorImage(author, author.Images.Single());
            Assert.That(recovered.State, Is.EqualTo("downloaded"));
            Assert.That(disk.BinaryByPath[variantPath], Is.EqualTo(originalBytes));
            Assert.That(MediaCoverRendition.StoredContentHashIsVerified(verificationPath, diskProvider), Is.True);
            var callsAfterRecovery = responses.Calls;
            await subject.EnsureAuthorImage(author, author.Images.Single());
            Assert.That(responses.Calls, Is.EqualTo(callsAfterRecovery));

            author.SelectedPosterHash = MediaCoverRendition.ComputeStableAuthorImageHash(remoteUrl, MediaCoverTypes.Poster);
            var resource = author.ToResource();
            subject.ConvertToLocalUrls(resource.Id, MediaCoverEntity.Author, resource.Images, author.SelectedPosterHash);
            Assert.That(resource.Images.Single().Url, Is.EqualTo($"/MediaCover/{authorId}/poster-{urlHash}{sourceExtension}"));
            Assert.That(resource.Images.Single().RemoteUrl, Is.EqualTo(remoteUrl));
            Assert.That(proxy.ProxyCalls, Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task on_demand_author_image_should_download_when_canonical_rendition_has_no_matching_verified_identity(bool hasDifferentIdentity, bool providerFails)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var responses = (ImageHttpClientProxy)(object)httpClient;
            const string remoteUrl = "https://assets.hardcover.app/authors/900011/target.jpg";
            const string differentUrl = "https://assets.hardcover.app/authors/900011/Target.jpg";
            const int authorId = 900011;
            var authorFolder = Path.Combine(ConfigRoot, "MediaCover", authorId.ToString());
            var identityPath = Path.Combine(authorFolder, "poster.url");
            var cached250Path = Path.Combine(authorFolder, "poster-250.jpg");
            var cached500Path = Path.Combine(authorFolder, "poster-500.jpg");
            var cachedBytes = BuildPngBytes(32, 48, new Rgba32(210, 40, 30));
            var responseBytes = BuildPngBytes(32, 48, new Rgba32(20, 120, 210));
            var urlHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(remoteUrl))).ToLowerInvariant()[..16];
            var variantPath = Path.Combine(authorFolder, $"poster-{urlHash}.jpg");
            var verificationPath = variantPath + ".sha256";
            disk.ExistingPaths.UnionWith(new[] { cached250Path, cached500Path });
            disk.BinaryByPath[cached250Path] = cachedBytes;
            disk.BinaryByPath[cached500Path] = cachedBytes;
            if (hasDifferentIdentity)
            {
                disk.ExistingPaths.Add(identityPath);
                disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(differentUrl, VerifiedRealImageHash);
            }
            responses.ResponseData = responseBytes;
            responses.FailureStatus = providerFails ? HttpStatusCode.Forbidden : null;
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = authorId,
                Name = "Example Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, remoteUrl)
                }
            };

            var result = await subject.EnsureAuthorImage(author, author.Images.Single());
            if (providerFails)
            {
                Assert.That(result.ErrorCode, Is.EqualTo("download_failed"));
                Assert.That(disk.ExistingPaths, Does.Not.Contain(variantPath));
                Assert.That(responses.Calls, Is.EqualTo(1));
                return;
            }

            var hasFetchedVariant = disk.BinaryByPath.TryGetValue(variantPath, out var fetchedBytes);
            var hasVerificationHash = disk.TextByPath.TryGetValue(verificationPath, out var verificationHash);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo("downloaded"));
                Assert.That(result.Path, Is.EqualTo(variantPath));
                Assert.That(hasFetchedVariant, Is.True);
                if (hasFetchedVariant)
                {
                    Assert.That(fetchedBytes, Is.EqualTo(responseBytes));
                    Assert.That(fetchedBytes, Is.Not.EqualTo(cachedBytes));
                }
                Assert.That(hasVerificationHash, Is.True);
                if (hasVerificationHash)
                {
                    Assert.That(verificationHash, Is.EqualTo(MediaCoverRendition.OriginalImageVerificationPrefix + MediaCoverRendition.ComputeContentSha256(responseBytes)));
                }
                Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.EqualTo(1));
            });
        }

        [TestCase("https://IMAGES.EXAMPLE/Portrait.jpg?size=Large", true)]
        [TestCase("HTTPS://images.example/Portrait.jpg?size=Large", true)]
        [TestCase("https://images.example/portrait.jpg?size=Large", false)]
        [TestCase("https://images.example/Portrait.jpg?size=large", false)]
        [TestCase("https://images.example/Portrait.jpg?Size=Large", false)]
        [TestCase("http://images.example/Portrait.jpg?size=Large", false)]
        public void author_photo_identity_preserves_path_and_query_case(string requestedUrl, bool matches)
        {
            const string storedUrl = "https://images.example/Portrait.jpg?size=Large";
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(storedUrl, VerifiedRealImageHash);
            Assert.That(new CoverMapperStub { DiskProvider = diskProvider }.AuthorCoverMatchesRemoteUrl(12, MediaCoverTypes.Poster, requestedUrl, out _), Is.EqualTo(matches));

            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(proxy, null, null, null, diskProvider, appFolder, null, DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>(), null, null, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var covers = new[] { new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, requestedUrl) };
            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);
            Assert.That(covers[0].Url.StartsWith("/MediaCover/", StringComparison.Ordinal), Is.EqualTo(matches));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(matches ? 0 : 1));
        }

        [Test]
        public void book_cover_candidates_should_come_only_from_the_monitored_edition()
        {
            var book = new Book
            {
                Id = 12,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 1,
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/unmonitored.jpg")
                        }
                    },
                    new()
                    {
                        Id = 2,
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/monitored.png")
                        }
                    }
                }
            };

            var selected = MediaCoverRendition.SelectMonitoredBookCovers(book);

            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected.Single().Edition.Id, Is.EqualTo(2));
            Assert.That(selected.Single().Cover.Url, Is.EqualTo("https://images.example/monitored.png"));
        }

        [Test]
        public void unmonitored_editions_should_never_be_borrowed_when_the_monitored_edition_has_no_cover()
        {
            var book = new Book
            {
                Id = 12,
                Editions = new List<Edition>
                {
                    new() { Id = 1, Monitored = true },
                    new()
                    {
                        Id = 2,
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/unmonitored.jpg")
                        }
                    }
                }
            };

            Assert.That(MediaCoverRendition.SelectMonitoredBookCovers(book), Is.Empty);
        }

        [Test]
        public void converting_a_book_without_a_cover_folder_should_not_probe_for_its_metadata_file()
        {
            // IDiskProvider.FileExists enumerates the whole MediaCover/Books directory when a file is
            // missing, so a book with no cover folder must be answered from FolderExists alone.
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.MissingFolders.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "900"));
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(null, null, null, null, diskProvider, appFolder, null, config, null, deferred, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var covers = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new() { CoverType = MediaCoverTypes.Cover, Url = "cover.jpg" }
            };

            subject.ConvertToLocalUrls(900, MediaCoverEntity.Book, covers);
            subject.ConvertToLocalUrls(900, MediaCoverEntity.Book, covers);

            Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.Zero);
            Assert.That(covers[0].Url, Is.EqualTo("cover.jpg"));
        }

        [Test]
        public void a_concurrent_cover_write_should_not_be_overwritten_by_the_missing_folder_negative_cache_entry()
        {
            // The reader stats the folder (missing), a cover download then stores its metadata, and only afterwards does the
            // reader cache its negative result. A plain indexer assignment let the stale negative clobber the fresh entry.
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.MissingFolders.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "901"));
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(null, null, null, null, diskProvider, appFolder, null, config, null, deferred, LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));

            const System.Reflection.BindingFlags nonPublic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var cache = (System.Collections.IDictionary)typeof(MediaCoverService).GetField("_bookCoverMetadataByBookId", nonPublic).GetValue(subject);
            var metadataType = typeof(MediaCoverService).GetNestedType("BookCoverMetadata", System.Reflection.BindingFlags.NonPublic);
            var editionType = typeof(MediaCoverService).GetNestedType("BookCoverMetadataSelectedEdition", System.Reflection.BindingFlags.NonPublic);
            disk.OnFolderExists = _ =>
            {
                var edition = Activator.CreateInstance(editionType);
                editionType.GetProperty("EditionProviderId").SetValue(edition, "gr:1");
                var fresh = Activator.CreateInstance(metadataType);
                metadataType.GetProperty("SelectedEdition").SetValue(fresh, edition);
                cache[901] = fresh;
            };
            var covers = new List<NzbDrone.Core.MediaCover.MediaCover> { new() { CoverType = MediaCoverTypes.Cover, Url = "cover.jpg" } };

            subject.ConvertToLocalUrls(901, MediaCoverEntity.Book, covers);

            var stored = cache[901];
            Assert.That(metadataType.GetProperty("SelectedEdition").GetValue(stored), Is.Not.Null, "the fresh positive entry was overwritten by the stale negative one");
        }

        [Test]
        public void selecting_a_coverless_edition_should_remove_the_previous_edition_local_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"SelectedEdition\":{\"LocalEditionId\":1,\"EditionProviderId\":\"gr:edition:old\",\"CoverUrl\":\"https://images.example/old.jpg\"}}";
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(
                null,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                deferred,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var book = new Book
            {
                Id = 12,
                Title = "Coverless edition",
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 2,
                        ForeignEditionId = "gr:edition:new",
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
                    }
                }
            };

            subject.EnsureBookCovers(book);

            Assert.That(disk.DeletedFolders, Is.EqualTo(new[] { Path.Combine(ConfigRoot, "MediaCover", "Books", "12") }));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg")));
        }

        [Test]
        public void transient_cover_absence_for_the_same_edition_should_retain_the_previous_local_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"SelectedEdition\":{\"EditionProviderId\":\"gr:edition:same\",\"CoverUrl\":\"https://images.example/old.jpg\"}}";
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(
                null,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                deferred,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var book = new Book
            {
                Id = 12,
                Title = "Temporarily unavailable cover",
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 2,
                        ForeignEditionId = "gr:edition:same",
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
                    }
                }
            };

            subject.EnsureBookCovers(book);

            Assert.That(disk.DeletedFolders, Is.Empty);
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg")));
        }

        [TestCase("cover-250.jpg", "cover.jpg")]
        [TestCase("cover-500.jpeg", "cover.jpeg")]
        [TestCase("cover-250.webp", "cover.webp")]
        [TestCase("cover-250.avif", "cover.avif")]
        [TestCase("cover-250.jfif", "cover.jfif")]
        public void rendition_fallback_should_not_depend_on_an_extension_allowlist(string rendition, string original)
        {
            var folder = Path.Combine(ConfigRoot, "MediaCover", "Books", "12");

            Assert.That(MediaCoverRendition.GetOriginalPath(Path.Combine(folder, rendition)), Is.EqualTo(Path.Combine(folder, original)));
        }

        [TestCase("/MediaCover/Books/12/cover-250.jpeg?lastWrite=1234")]
        [TestCase("/MediaCover/Books/12/cover-250.jfif")]
        [TestCase("/MediaCover/Books/12/cover-250.webp")]
        public void local_cover_routes_should_accept_the_shared_supported_image_extensions(string path)
        {
            Assert.That(MediaCoverRendition.IsSupportedImagePath(path), Is.True);
        }

        [TestCase("/MediaCover/Books/12/cover-metadata.json")]
        [TestCase("/MediaCover/Books/12/readme.txt")]
        [TestCase("/MediaCover/Books/12/no-extension")]
        public void local_cover_routes_should_not_expose_non_image_files(string path)
        {
            Assert.That(MediaCoverRendition.IsSupportedImagePath(path), Is.False);
        }

        [Test]
        public void books_page_jpeg_rendition_request_should_fall_back_to_the_real_original()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpeg"));
            var mapper = new MediaCoverMapper(appFolder, diskProvider, LogManager.GetCurrentClassLogger());

            var mapped = mapper.Map("/MediaCover/Books/12/cover-500.jpeg");

            Assert.That(mapped, Is.EqualTo(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpeg")));
            Assert.That(mapper.CanHandle("/MediaCover/Books/12/cover-metadata.json"), Is.False);
        }

        [TestCase("12/poster-250.png", "12/poster-500.png", -1, true, false)]
        [TestCase("12/poster-250.png", "12/poster-500.png", 0, true, false)]
        [TestCase("12/poster-250.png", "12/poster-500.png", 1, true, true)]
        [TestCase("12/poster-500.png", "12/poster-250.png", -1, true, false)]
        [TestCase("12/poster-250.png", "12/poster.png", -1, true, true)]
        [TestCase("12/poster.png", "12/poster-500.png", -1, true, false)]
        [TestCase("12/poster-1234567890123456.png", "12/poster-500.png", -1, false, false)]
        [TestCase("12/poster-97708e34485fdce4.png", "12/poster-500.png", -1, false, false)]
        [TestCase("12/banner-35.png", "12/poster-500.png", -1, false, false)]
        [TestCase("12/poster-250.png", "13/poster-500.png", -1, false, false)]
        [TestCase("Books/12/cover-250.png", "Books/12/cover-500.png", -1, false, false)]
        [TestCase("12/banner-35.png", "12/banner-70.png", -1, true, false)]
        [TestCase("12/fanart-360.png", "12/fanart-180.png", -1, true, false)]
        [TestCase("12/poster-250.png", null, 0, false, false)]
        [TestCase("12/poster.png", null, 0, false, false)]
        [TestCase("12/poster-97708e34485fdce4.png", null, 0, false, false)]
        [TestCase("Books/12/cover.png", null, 0, false, false)]
        [TestCase("12/poster-250.avif", "12/poster.avif", -1, true, true)]
        [TestCase("Books/12/cover-250.png", "Books/12/cover.png", -1, true, true)]
        public async Task local_author_photo_request_serves_an_existing_size_without_browser_recovery(
            string requested, string surviving, int requestedSize, bool available, bool cacheable)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-local-photo-" + Guid.NewGuid().ToString("N"));
            try
            {
                var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
                ((AppFolderProxy)(object)appFolder).AppDataFolder = folder;
                var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
                var disk = (DiskProviderProxy)(object)diskProvider;
                var requestedPath = Path.Combine(folder, "MediaCover", requested.Replace('/', Path.DirectorySeparatorChar));
                var survivingPath = surviving == null ? null : Path.Combine(folder, "MediaCover", surviving.Replace('/', Path.DirectorySeparatorChar));
                // A valid 16x24 AVIF generated with ffmpeg/libaom, not PNG bytes with an AVIF suffix.
                var avif = requested.EndsWith(".avif", StringComparison.Ordinal);
                var survivingBytes = avif ? Convert.FromBase64String(
                    "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAcAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAQAAAAGAAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAkbWRhdAoGGA0+7YBAMhIYAAAAUAAAAAINg0uQg2xym8g=")
                    : BuildPngBytes(32, 48, new Rgba32(20, 120, 210));
                var requestedBytes = BuildPngBytes(16, 24, new Rgba32(210, 120, 20));
                Directory.CreateDirectory(Path.GetDirectoryName(requestedPath));
                if (survivingPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(survivingPath));
                    File.WriteAllBytes(survivingPath, survivingBytes);
                    disk.ExistingPaths.Add(survivingPath);
                    disk.BinaryByPath[survivingPath] = survivingBytes;
                }
                if (requestedSize >= 0)
                {
                    var bytes = requestedSize == 0 ? Array.Empty<byte>() : requestedBytes;
                    File.WriteAllBytes(requestedPath, bytes);
                    disk.ExistingPaths.Add(requestedPath);
                    disk.BinaryByPath[requestedPath] = bytes;
                }

                if (avif)
                {
                    Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(survivingBytes, "image/avif"));
                    Assert.Throws<UnknownImageFormatException>(() => new ImageResizer(diskProvider).Resize(survivingPath, requestedPath, 250));
                    Assert.That(File.Exists(survivingPath), Is.True);
                    Assert.That(File.Exists(requestedPath), Is.False);
                }

                var messages = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                using var logs = new LogFactory { Configuration = logging };
                var mapper = new MediaCoverMapper(appFolder, diskProvider, logs.GetLogger("LocalPhotoFixture"));
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddControllers();
                using var provider = services.BuildServiceProvider();
                var (response, body) = await ExecuteLocalCoverRequest(mapper, provider, requested);
                Assert.That(response.Response.StatusCode, Is.EqualTo(available ? 200 : 404),
                    "Serve a usable photo immediately, but never an empty file or another photo");
                Assert.That(body, Is.EqualTo(available ? requestedSize > 0 ? requestedBytes : survivingBytes : Array.Empty<byte>()));
                Assert.That(response.Response.Headers.CacheControl.ToString(), Is.EqualTo(cacheable
                    ? "max-age=31536000, public" : "no-cache, no-store, must-revalidate"));
                if (available)
                {
                    Assert.That(response.Response.ContentType, Is.EqualTo(avif ? "image/avif" : "image/png"));
                }

                if (requestedSize == 0 && surviving == null)
                {
                    Assert.That(messages.Logs, Does.Contain($"Debug|Not serving empty media cover: /MediaCover/{requested}"));
                }
                else if (available && requestedSize <= 0)
                {
                    Assert.That(messages.Logs, Does.Contain($"Debug|Serving media cover substitute: /MediaCover/{requested} -> {Path.GetFileName(surviving)}; source: {(cacheable ? "original (default caching)" : "rendition (no-store)")}"));
                }

                if (cacheable)
                {
                    var (repeated, repeatedBody) = await ExecuteLocalCoverRequest(mapper, provider, requested);
                    Assert.That(repeated.Response.StatusCode, Is.EqualTo(200));
                    Assert.That(repeatedBody, Is.EqualTo(body));
                    Assert.That(repeated.Response.Headers.CacheControl.ToString(), Is.EqualTo("max-age=31536000, public"));
                    var (conditional, conditionalBody) = await ExecuteLocalCoverRequest(
                        mapper, provider, requested, response.Response.Headers.LastModified.ToString());
                    Assert.That(conditional.Response.StatusCode, Is.EqualTo(304));
                    Assert.That(conditionalBody, Is.Empty, "Existing middleware returns 304 for the supplied validator; it does not compare file bytes");
                }
                else
                {
                    Assert.That(response.Response.Headers.ContainsKey("Last-Modified"), Is.False);
                    Assert.That(response.Response.Headers.ContainsKey("ETag"), Is.False);

                    File.WriteAllBytes(requestedPath, requestedBytes);
                    disk.ExistingPaths.Add(requestedPath);
                    disk.BinaryByPath[requestedPath] = requestedBytes;
                    // No stored fallback or validator: the same URL must now serve the restored file, not a 304.
                    var (restored, restoredBody) = await ExecuteLocalCoverRequest(mapper, provider, requested);
                    Assert.That(restored.Response.StatusCode, Is.EqualTo(200));
                    Assert.That(restoredBody, Is.EqualTo(requestedBytes));
                    Assert.That(restored.Response.Headers.CacheControl.ToString(), Is.EqualTo("max-age=31536000, public"));
                    Assert.That(restored.Response.Headers.ContainsKey("Last-Modified"), Is.True);
                }

                Assert.That(disk.GetCallCount(nameof(IDiskProvider.SaveStream)), Is.Zero);
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.GetFiles)), Is.Zero, "Use the bounded size list, not directory enumeration");
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }

        private static async Task<(DefaultHttpContext Context, byte[] Body)> ExecuteLocalCoverRequest(
            MediaCoverMapper mapper, IServiceProvider services, string requested, string ifModifiedSince = null)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/MediaCover/" + requested;
            context.Request.QueryString = new QueryString("?v=same-photo-hash");
            if (ifModifiedSince != null)
            {
                context.Request.Headers.IfModifiedSince = ifModifiedSince;
            }
            using var body = new MemoryStream();
            context.Response.Body = body;
            var policy = new CacheHeaderMiddlewareFixture.ResourceCacheSpecification();
            var conditional = new IfModifiedMiddleware(async http =>
            {
                var controller = new StaticResourceController(new[] { mapper }, LogManager.GetCurrentClassLogger())
                {
                    ControllerContext = new ControllerContext { HttpContext = http }
                };
                await controller.Index("MediaCover/" + requested).ExecuteResultAsync(
                    new ActionContext(http, new RouteData(), new ActionDescriptor()));
            }, policy);

            await conditional.InvokeAsync(context);
            // DefaultHttpContext has no server to fire OnStarting; apply the actual header callback explicitly.
            new CacheHeaderMiddleware(_ => Task.CompletedTask, policy).ApplyCacheHeaders(context);
            return (context, body.ToArray());
        }

        private sealed class CoverRefreshHarness : IDisposable
        {
            private readonly LogFactory _logs = new();
            public readonly NLog.Targets.MemoryTarget Logs = new() { Layout = "${level}|${message}" };
            public readonly IDiskProvider DiskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            public readonly IAppFolderInfo AppFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            public readonly IHttpClient Http = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            public MediaCoverService Subject;
            public readonly IDeferredCoverService Deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            private readonly IImageResizer _resizer;
            private readonly string _root;
            public readonly Author Author = new() { Id = 12, Name = "Photo owner" };
            public readonly Book Book;
            public readonly bool AuthorCover;
            public readonly NzbDrone.Core.Http.HttpServiceHealth HostHealth = new(LogManager.GetCurrentClassLogger());
            public int? FailedHeight;
            public int Resizes;
            public bool Undecodable;
            public Action BeforeResize;
            public DiskProviderProxy Disk => (DiskProviderProxy)(object)DiskProvider;
            public ImageHttpClientProxy Responses => (ImageHttpClientProxy)(object)Http;
            public NzbDrone.Core.MediaCover.MediaCover Cover => AuthorCover ? Author.Images.First() : Book.Editions.Single(edition => edition.Monitored).Images.First();
            public string Original => Path.Combine(_root, "MediaCover", AuthorCover ? "12" : Path.Combine("Books", "12"),
                (AuthorCover ? "poster" : "cover") + Cover.Extension);
            public string Sized(int height) => Path.Combine(Path.GetDirectoryName(Original),
                Path.GetFileNameWithoutExtension(Original) + "-" + height + Cover.Extension);

            public CoverRefreshHarness(bool authorCover, string root = null)
            {
                AuthorCover = authorCover;
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, Logs);
                _logs.Configuration = logging;
                _root = root ?? ConfigRoot;
                ((AppFolderProxy)(object)AppFolder).AppDataFolder = _root;
                _resizer = InterfaceProxy.Proxy<IImageResizer>((_, args) =>
                {
                    Resizes++;
                    BeforeResize?.Invoke();
                    if (Undecodable) throw new UnknownImageFormatException("Fixture AVIF resize");
                    if ((int)args[2] == FailedHeight) throw new IOException("Replacement size unavailable");
                    Disk.BinaryByPath[(string)args[1]] = Disk.BinaryByPath[(string)args[0]].ToArray();
                    Disk.ExistingPaths.Add((string)args[1]);
                    return null;
                });
                Restart();
                Book = new Book { Id = 12, Title = "Book cover", Author = Author, Editions = new List<Edition>
                {
                    new() { Id = 13, ForeignEditionId = "gr:edition:13", Monitored = true }
                } };
                SetPhoto("first.png", BuildPngBytes(24, 32, new Rgba32(20, 100, 200)));
            }

            public List<Book> RepairBooks;
            public int RepairPageReads;
            public int RepairIdReads;
            public Exception RepairIdFailure;
            public Action BeforeRepairPage;
            public bool LibraryOnly;
            public HashSet<int> LibraryIds = new();
            public void Restart()
            {
                var books = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.GetBookIds) => ReadRepairIds(),
                    nameof(IBookService.GetLibraryBookIds) => ((IEnumerable<int>)args[0] ?? LibraryIds).Where(LibraryIds.Contains).ToList(),
                    nameof(IBookService.GetExistingBooks) => ReadRepairPage((IEnumerable<int>)args[0]),
                    _ => throw new NotImplementedException(method.Name)
                });
                var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name switch
                {
                    "get_CoverDownloadScope" => LibraryOnly ? CoverDownloadScope.LibraryBooks : CoverDownloadScope.AllBooks,
                    "get_MetadataServerUrl" => "https://images.example",
                    _ => throw new NotImplementedException(method.Name)
                });
                Subject = new MediaCoverService(new MediaCoverProxyStub(), _resizer, books, Http, DiskProvider, AppFolder, config, DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>(), null, Deferred, _logs.GetLogger("CoverRefreshFixture"), authorService: DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(), imageHostHealth: HostHealth);
            }

            private List<int> ReadRepairIds()
            {
                RepairIdReads++;
                if (RepairIdFailure != null) throw RepairIdFailure;
                return (RepairBooks ?? new() { Book }).Select(book => book.Id).ToList();
            }

            private List<Book> ReadRepairPage(IEnumerable<int> ids)
            {
                RepairPageReads++;
                BeforeRepairPage?.Invoke();
                var page = ids.ToHashSet();
                Assert.That(page.Count, Is.LessThanOrEqualTo(250));
                return (RepairBooks ?? new() { Book }).Where(book => page.Contains(book.Id)).ToList();
            }

            public void SetPhoto(string file, byte[] bytes)
            {
                var cover = new NzbDrone.Core.MediaCover.MediaCover(AuthorCover ? MediaCoverTypes.Poster : MediaCoverTypes.Cover,
                    "https://images.example/" + file);
                Author.Images = new() { cover };
                Book.Editions.Single().Images = new() { cover };
                Responses.ResponseData = bytes;
            }

            public void Refresh()
            {
                if (AuthorCover) Subject.EnsureAuthorCovers(Author);
                else Subject.EnsureBookCovers(Book);
            }

            public string LocalUrl()
            {
                var copy = new NzbDrone.Core.MediaCover.MediaCover(Cover.CoverType, Cover.Url);
                Subject.ConvertToLocalUrls(12, AuthorCover ? MediaCoverEntity.Author : MediaCoverEntity.Book, new[] { copy });
                return copy.Url;
            }

            public void Dispose() => _logs.Dispose();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void refused_original_publication_preserves_previous_renditions(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var oldSize = h.Disk.BinaryByPath[h.Sized(250)].ToArray();
            h.Disk.BinaryByPath[h.Sized(500)] = Array.Empty<byte>();
            h.SetPhoto("replacement.png", BuildPngBytes(24, 32, new Rgba32(150, 40, 20)));
            h.Disk.RefusePublication = true;
            h.Refresh();
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(oldSize));
            Assert.That(h.Disk.ExistingPaths, Does.Contain(h.Sized(500)));
            Assert.That(h.Disk.ExistingPaths.Any(path => Path.GetFileName(path).StartsWith(DiskProviderBase.TemporaryWritePrefix)), Is.False);
        }

        [Test]
        public void overlapping_author_refresh_cannot_mix_one_photos_receipt_with_anothers_pixels()
        {
            using var h = new CoverRefreshHarness(true);
            var first = h.Responses.ResponseData.ToArray();
            var firstUrl = h.Cover.Url;
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), "poster.url");
            h.Disk.BeforeWriteText = path =>
            {
                if (path != identity) return;
                h.Disk.BeforeWriteText = null;
                h.SetPhoto("concurrent.png", BuildPngBytes(24, 32, new Rgba32(150, 40, 20)));
                h.Refresh();
            };
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(1), "An overlapping replacement must not start another download");
            Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(h.Disk.TextByPath[identity], out var url, out var hash), Is.True);
            Assert.That(url, Is.EqualTo(firstUrl));
            Assert.That(hash, Is.EqualTo(MediaCoverRendition.ComputeContentSha256(first)));
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(first));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(first));
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(2), "The subsequent refresh must still apply the newer metadata");
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void invalid_author_image_is_not_certified_and_the_next_provider_is_tried()
        {
            using var h = new CoverRefreshHarness(true);
            h.Author.Images.Add(new(MediaCoverTypes.Poster, "https://images.example/fallback.png"));
            h.Responses.ResponseQueue.Enqueue(Encoding.UTF8.GetBytes("<html>CDN error</html>"));
            h.Responses.ResponseQueue.Enqueue(h.Responses.ResponseData);
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), "poster.url");
            Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(h.Disk.TextByPath[identity], out var url, out _), Is.True);
            Assert.That(url, Is.EqualTo("https://images.example/fallback.png"));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void author_republish_uses_a_new_token_but_size_repair_keeps_it()
        {
            using var h = new CoverRefreshHarness(true);
            h.Undecodable = true; // Keep the original, as an unsupported resizer would.
            h.Refresh();
            var first = h.LocalUrl();
            var original = h.Responses.ResponseData;
            h.SetPhoto("second.png", BuildPngBytes(24, 32, new Rgba32(200, 60, 20)));
            h.Refresh();
            var second = h.LocalUrl();
            h.SetPhoto("first.png", original);
            h.Refresh();
            var reverted = h.LocalUrl();
            Assert.That(reverted, Is.Not.EqualTo(first).And.Not.EqualTo(second));
            var requests = h.Responses.Calls;
            h.Undecodable = false;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests));
            Assert.That(h.LocalUrl(), Is.EqualTo(reverted));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(original));
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void refused_publication_or_receipt_delete_keeps_previous_receipt_and_local_url(bool authorCover, bool refuseDelete)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var oldUrl = h.LocalUrl();
            var receipt = h.Disk.TextByPath.Single();
            var oldSmall = h.Disk.BinaryByPath[h.Sized(250)].ToArray();
            h.Disk.ExistingPaths.Remove(h.Sized(500));
            h.SetPhoto("replacement.png", BuildPngBytes(24, 32, new Rgba32(200, 60, 20)));
            if (refuseDelete) h.Disk.BeforeDelete = path => { if (path == receipt.Key) throw new IOException("Fixture receipt locked"); };
            else h.Disk.RefusePublication = true;
            h.Refresh();
            Assert.That(h.Disk.TextByPath[receipt.Key], Is.EqualTo(receipt.Value));
            h.SetPhoto("first.png", oldSmall);
            Assert.That(h.LocalUrl(), Is.EqualTo(oldUrl));
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(oldSmall));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void forced_request_does_not_interrupt_an_active_publication(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Disk.BeforeWriteText = _ =>
            {
                h.Disk.BeforeWriteText = null;
                if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, forceDownload: true);
                else h.Subject.EnsureBookCovers(h.Book, forceDownload: true);
            };
            h.Refresh();
            Assert.That(h.Disk.DeletedFolders, Is.Empty);
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task carousel_rejects_html_without_certifying_it()
        {
            using var h = new CoverRefreshHarness(true);
            h.Responses.ResponseData = Encoding.UTF8.GetBytes("<html>CDN error</html>");
            var result = await h.Subject.EnsureAuthorImage(h.Author, h.Cover);
            Assert.That(result.State, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("invalid_image"));
            Assert.That(h.Disk.TextByPath, Is.Empty);
            Assert.That(h.Disk.BinaryByPath, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void forced_provider_failure_keeps_all_existing_pixels_and_receipts(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var pixels = h.Disk.BinaryByPath.ToDictionary(p => p.Key, p => p.Value.ToArray());
            var receipts = h.Disk.TextByPath.ToDictionary(p => p.Key, p => p.Value);
            h.Responses.FailureStatus = HttpStatusCode.ServiceUnavailable;
            if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
            else h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(2), "Force must really attempt the download");
            Assert.That(h.Disk.DeletedFolders, Is.Empty);
            Assert.That(h.Disk.TextByPath, Is.EquivalentTo(receipts));
            Assert.That(h.Disk.BinaryByPath.Keys, Is.EquivalentTo(pixels.Keys));
            foreach (var pair in pixels) Assert.That(h.Disk.BinaryByPath[pair.Key], Is.EqualTo(pair.Value));
            h.Responses.FailureStatus = null;
            {
                if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
                else h.Subject.EnsureBookCovers(h.Book, true);
                Assert.That(h.Responses.Calls, Is.EqualTo(2), "Force respects the host pause and keeps the old cover");
                h.HostHealth.GetStatus("image:https://images.example").RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            }
            h.Responses.ResponseData = BuildPngBytes(24, 32, new Rgba32(210, 40, 50));
            if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
            else h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(3));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void forced_failure_does_not_replace_the_stored_photo_with_another_provider(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var old = h.Disk.BinaryByPath[h.Sized(500)].ToArray();
            var preferredUrl = h.Cover.Url;
            var fallback = new NzbDrone.Core.MediaCover.MediaCover(h.Cover.CoverType, "https://fallback.example/new.png");
            if (authorCover) h.Author.Images.Add(fallback);
            else h.Book.Editions.Single().Images.Add(fallback);
            h.Responses.FailureForRequest = request => request.Url.FullUri == preferredUrl ? HttpStatusCode.ServiceUnavailable : null;
            h.Responses.ResponseData = BuildPngBytes(24, 32, new Rgba32(200, 20, 10));
            if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
            else h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(old));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void forced_placeholder_retains_the_real_photo_on_this_and_the_next_refresh(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.SetPhoto($"temporary-placeholder-{authorCover}.png", h.Responses.ResponseData);
            h.Refresh();
            var storedPath = h.Sized(500);
            var old = h.Disk.BinaryByPath[storedPath].ToArray();
            var receipts = h.Disk.TextByPath.ToDictionary(p => p.Key, p => p.Value);
            h.Responses.ResponseData = Convert.FromBase64String(MascotWebpBase64);
            if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
            else h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.ExistingPaths, Does.Contain(storedPath));
            Assert.That(h.Disk.BinaryByPath[storedPath], Is.EqualTo(old));
            Assert.That(h.Disk.TextByPath, Is.EquivalentTo(receipts));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(h.Cover.Url), Is.False,
                "A transient answer must not make the retained real photo disappear on the next pass");
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.ExistingPaths, Does.Contain(storedPath));
            Assert.That(h.Disk.BinaryByPath[storedPath], Is.EqualTo(old));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void unreadable_receipt_cannot_delete_the_image_or_its_url(bool denied, bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.SetPhoto("unreadable-photo-" + denied + "-" + authorCover + ".png", h.Responses.ResponseData);
            h.Refresh();
            var path = h.Sized(500);
            var pixels = h.Disk.BinaryByPath[path].ToArray();
            var receipt = h.Disk.TextByPath.Keys.Single();
            var url = h.Cover.Url;
            h.Restart();
            h.Disk.BeforeReadText = _ => { if (denied) throw new UnauthorizedAccessException("Fixture receipt denied"); throw new IOException("Fixture receipt locked"); };
            h.Responses.ResponseData = Convert.FromBase64String(MascotWebpBase64);
            if (authorCover) h.Subject.EnsureAuthorCovers(h.Author, true);
            else h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(1), "Do not replace or delete an image whose receipt cannot be read");
            Assert.That(h.Disk.ExistingPaths, Does.Contain(path));
            Assert.That(h.Disk.BinaryByPath[path], Is.EqualTo(pixels));
            Assert.That(h.Author.Images.Select(x => x.Url), Is.EqualTo(new[] { url }));
            Assert.That(h.Logs.Logs.Count(line => line.StartsWith("Warn|Cannot read the cover receipt")), Is.EqualTo(1));
            h.Disk.BeforeReadText = null;
            h.Refresh();
            Assert.That(h.LocalUrl(), Does.StartWith("/MediaCover/"));
            Assert.That(h.Disk.TextByPath, Does.ContainKey(receipt));
        }

        [Test]
        public void forced_placeholder_with_corrupt_receipt_keeps_photo_and_url()
        {
            using var h = new CoverRefreshHarness(true);
            h.SetPhoto("corrupt-forced.png", h.Responses.ResponseData);
            h.Refresh();
            var old = h.Disk.BinaryByPath[h.Sized(500)].ToArray();
            h.Disk.TextByPath[h.Disk.TextByPath.Keys.Single()] = "";
            h.Restart();
            h.Responses.ResponseData = Convert.FromBase64String(MascotWebpBase64);
            h.Subject.EnsureAuthorCovers(h.Author, true);
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(old));
            Assert.That(h.Author.Images.Single().Url, Is.EqualTo(h.Cover.Url));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(h.Cover.Url), Is.False);
        }

        [Test]
        public void repair_warns_and_skips_an_unreadable_author_receipt()
        {
            using var h = new CoverRefreshHarness(true);
            h.SetPhoto("repair-unreadable.png", h.Responses.ResponseData);
            h.Refresh(); h.Restart();
            h.Disk.BeforeReadText = _ => throw new UnauthorizedAccessException("Fixture permissions");
            var authors = InterfaceProxy.Proxy<IAuthorService>((_, _) => new List<Author> { h.Author });
            var attempted = 0;
            var mapper = InterfaceProxy.Proxy<IMapCoversToLocal>((method, args) =>
            {
                if (method.Name == nameof(IMapCoversToLocal.EnsureAuthorCovers)) attempted++;
                return method.Invoke(h.Subject, args);
            });
            var repair = new RepairAuthorMediaCoversService(authors, mapper, h.DiskProvider,
                DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>(), LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>());
            repair.Execute(new RepairAuthorMediaCoversCommand());
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            Assert.That(attempted, Is.Zero, "An unreadable receipt does not establish missing photo sizes");
            Assert.That(h.Disk.ExistingPaths, Does.Contain(h.Sized(500)));
        }


        [Test]
        public void display_uses_the_stored_cover_without_edition_identity()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Refresh();
            var expected = h.LocalUrl();
            var covers = new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, h.Cover.Url) };
            h.Subject.ConvertBookCoversToLocalUrls(new[] { new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(12, covers) });
            Assert.That(covers[0].Url, Is.EqualTo(expected).And.Contain("/MediaCover/Books/12/"));
        }

        [Test]
        public void same_edition_stale_cover_is_not_a_banner_or_fanart()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Refresh();
            var covers = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new(MediaCoverTypes.Banner, "https://images.example/banner.png"),
                new(MediaCoverTypes.Fanart, "https://images.example/fanart.png")
            };
            h.Subject.ConvertBookCoversToLocalUrls(new[] { new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(12, covers) });
            Assert.That(covers.Select(c => c.Url), Has.All.Not.Contains("/MediaCover/Books/"));
        }

        [Test]
        public void invalid_edition_switch_never_certifies_the_previous_editions_pixels()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Refresh();
            h.Book.Editions.Single().Asin = "B002V0QUOC";
            h.Book.Editions.Single().GoodreadsEditionId = 999;
            h.Book.Editions.Single().ForeignEditionId = "gr:edition:999";
            h.Book.Editions.Single().Id = 14;
            h.Cover.Url = "not-an-absolute-url.png";
            h.Refresh();
            var receipt = Path.Combine(Path.GetDirectoryName(h.Original), "cover-metadata.json");
            Assert.That(h.Disk.TextByPath.GetValueOrDefault(receipt), Is.Null,
                "Failed edition B must not take ownership of edition A's image");
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(h.Original), "Do not retain the proven different edition image");
        }

        [Test]
        public void paused_author_repair_and_on_demand_use_the_stored_photo_without_requests()
        {
            using var h = new CoverRefreshHarness(true);
            h.Refresh();
            var bytes = h.Disk.BinaryByPath[h.Sized(500)].ToArray();
            h.Disk.ExistingPaths.Remove(h.Sized(250));
            h.Disk.BinaryByPath.Remove(h.Sized(250));
            h.HostHealth.ReportRateLimited("image:https://images.example", TimeSpan.FromMinutes(1));
            var authors = InterfaceProxy.Proxy<IAuthorService>((method, _) => method.Name switch
            {
                nameof(IAuthorService.GetAllAuthors) or nameof(IAuthorService.GetExistingAuthors) => new List<Author> { h.Author },
                nameof(IAuthorService.RemoveKnownPlaceholderImages) => false,
                _ => throw new AssertionException(method.Name)
            });
            var repair = new RepairAuthorMediaCoversService(authors, h.Subject, h.DiskProvider,
                DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>(), LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>());
            repair.Execute(new RepairAuthorMediaCoversCommand());
            var result = h.Subject.EnsureAuthorImage(h.Author, h.Cover).GetAwaiter().GetResult();
            Assert.That(result.State, Is.EqualTo("downloaded"));
            Assert.That(h.Responses.Calls, Is.EqualTo(1), "Repair and on-demand must respect the shared host pause");
            Assert.That(h.Disk.BinaryByPath[result.Path], Is.EqualTo(bytes));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void paused_or_rejected_downloads_do_not_create_empty_book_folders(bool paused)
        {
            using var h = new CoverRefreshHarness(false);
            if (paused) h.HostHealth.ReportRateLimited("image:https://images.example", TimeSpan.FromMinutes(1));
            else h.Responses.FailureStatus = HttpStatusCode.NotFound;
            h.Refresh();
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.EnsureFolder)), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_hash_is_saved_atomically_once_and_survives_restart(bool refuseSave)
        {
            using var h = new CoverRefreshHarness(false);
            h.Refresh();
            var path = h.Disk.TextByPath.Keys.Single();
            var json = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[path]);
            json["selectedEdition"]["contentHash"] = null;
            var legacy = json.ToString();
            h.Disk.TextByPath[path] = legacy;
            h.Restart();
            h.Disk.RefusePublication = refuseSave;
            var writes = h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically));
            h.Subject.RepairBookCovers();
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically)), Is.EqualTo(writes + 1));
            if (refuseSave)
            {
                Assert.That(h.Disk.TextByPath[path], Is.EqualTo(legacy), "A failed swap keeps the complete old receipt");
                Assert.That(h.Logs.Logs, Has.Some.Contains("Unable to save the cover fingerprint"));
                h.Disk.RefusePublication = false;
                h.Subject.RepairBookCovers();
            }
            var updated = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[path]);
            Assert.That((string)updated["selectedEdition"]["contentHash"], Is.EqualTo(MediaCoverRendition.ComputeContentSha256(h.Responses.ResponseData)));
            updated["selectedEdition"]["contentHash"] = null;
            Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(updated, json), Is.True, "Preserve every receipt field and the browser version");
            writes = h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically));
            h.Restart();
            var reads = h.Disk.GetCallCount(nameof(IDiskProvider.OpenReadStream));
            h.Subject.RepairBookCovers();
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.OpenReadStream)), Is.EqualTo(reads));
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically)), Is.EqualTo(writes));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_receipt_repairs_missing_provider_identity_only_for_the_same_url(bool sameUrl)
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Refresh();
            var path = h.Disk.TextByPath.Keys.Single();
            var receipt = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[path]);
            var version = receipt["selectedEdition"]["downloadedAt"].ToString();
            receipt["selectedEdition"]["editionProviderId"] = null;
            ((Newtonsoft.Json.Linq.JObject)receipt["selectedEdition"]).Remove("editionProviderIds");
            h.Disk.TextByPath[path] = receipt.ToString();
            h.Restart();
            if (!sameUrl) h.Cover.Url = "https://images.example/new.png";
            h.Responses.FailureStatus = HttpStatusCode.InternalServerError;
            h.Subject.RepairBookCovers();
            receipt = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[path]);
            Assert.That((string)receipt["selectedEdition"]["editionProviderId"], Is.EqualTo(sameUrl ? "az:B07HPDQ9SS" : null));
            Assert.That(receipt["selectedEdition"]["downloadedAt"].ToString(), Is.EqualTo(version));
            Assert.That(h.Logs.Logs, Has.Some.Contains($"Info|[BOOK-COVER-REPAIR] 1 checked, {(sameUrl ? 0 : 1)} candidates, 0 repaired, {(sameUrl ? 0 : 1)} incomplete, 0 unreadable, {(sameUrl ? 0 : 1)} retained with unknown edition"));
            var writes = h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically));
            h.Restart(); h.Subject.RepairBookCovers();
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.WriteFileAtomically)), Is.EqualTo(writes));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_publication_keeps_only_the_current_editions_cover(bool editionChanged)
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Refresh();
            if (editionChanged)
            {
                h.Book.Editions.Single().Asin = "B002V0QUOC";
                h.Book.Editions.Single().ForeignEditionId = "gr:edition:14";
            }
            h.SetPhoto("publication-failure.jpg", h.Responses.ResponseData);
            h.Disk.RefusePublication = true;
            h.Refresh();
            var receipt = h.Disk.TextByPath.Keys.Any(path => path.EndsWith("cover-metadata.json"));
            Assert.That(receipt, Is.EqualTo(!editionChanged));
        }

        [Test]
        public void provider_equivalent_merge_keeps_the_cover_during_an_outage()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().Asin = "B07HPDQ9SS";
            h.Book.Editions.Single().HardcoverEditionId = "123";
            h.Refresh();
            var before = h.LocalUrl();
            h.Book.Editions.Single().Id = 61000;
            h.Book.Editions.Single().HardcoverEditionId = "456";
            h.Book.Editions.Single().ForeignEditionId = "hc:edition:456";
            h.SetPhoto("new-provider-url.jpg", h.Responses.ResponseData);
            h.Responses.FailureStatus = HttpStatusCode.InternalServerError;
            h.Refresh();
            Assert.That(h.LocalUrl(), Is.EqualTo(before));
            Assert.That(h.Subject.ReadLocalBookCover(h.Book.Editions.Single()).Ready, Is.False);
        }

        [Test]
        public void repair_bounds_large_catalog_hydration_in_existing_batches()
        {
            using var h = new CoverRefreshHarness(false);
            h.RepairBooks = Enumerable.Range(1, 60000).Select(id => new Book
            {
                Id = id, Editions = new() { new Edition { Id = id, Monitored = true, Images = new() } }
            }).ToList();
            h.Subject.RepairBookCovers();
            Assert.That(h.RepairPageReads, Is.EqualTo(240));
            Assert.That(h.Responses.Calls, Is.Zero);
        }

        [Test]
        public void daily_repair_uses_one_failed_host_probe_per_backoff_window()
        {
            using var h = new CoverRefreshHarness(false);
            h.RepairBooks = Enumerable.Range(1, 20).Select(id => new Book
            {
                Id = id, Title = "Fixture book", Editions = new List<Edition>
                {
                    new() { Id = id, Monitored = true, ForeignEditionId = "gr:" + id,
                        Images = new() { new(MediaCoverTypes.Cover, "https://images.example/repair-" + id + ".png") } }
                }
            }).ToList();
            h.Responses.Failure = new WebException("Fixture host timeout", WebExceptionStatus.Timeout);
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            h.HostHealth.GetStatus("image:https://images.example").RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
        }

        [Test]
        public void repair_during_import_performs_no_catalog_read_or_enqueue()
        {
            using var h = new CoverRefreshHarness(false);
            ((DeferredCoverProxy)(object)h.Deferred).ShouldDefer = true;
            h.Subject.RepairBookCovers();
            Assert.That(h.RepairIdReads, Is.Zero, "Import pause must prevent the ID query itself");
            Assert.That(h.RepairPageReads, Is.Zero);
            Assert.That(((DeferredCoverProxy)(object)h.Deferred).MarkedBookIds, Is.Empty);
            Assert.That(h.Logs.Logs, Has.Some.Contains("import running; repair resumes after the import"));
        }

        [TestCase("none")]
        [TestCase("error")]
        [TestCase("running")]
        public void import_completion_resumes_a_paused_repair_once_even_with_no_pending_books(string queueResult)
        {
            using var h = new CoverRefreshHarness(false);
            var deferred = (DeferredCoverProxy)(object)h.Deferred;
            deferred.ShouldDefer = true;
            h.Subject.RepairBookCovers();
            h.Subject.RepairBookCovers();
            Assert.That(h.RepairIdReads, Is.Zero);
            var queued = new List<NzbDrone.Core.Messaging.Commands.Command>();
            var attempts = 0;
            var queue = InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>((_, args) =>
            {
                attempts++;
                Assert.That(args[1], Is.EqualTo(NzbDrone.Core.Messaging.Commands.CommandPriority.Low));
                if (attempts == 1 && queueResult == "error") throw new IOException("Fixture queue save unavailable");
                if (attempts == 1 && queueResult == "running") return new NzbDrone.Core.Messaging.Commands.CommandModel { Status = NzbDrone.Core.Messaging.Commands.CommandStatus.Started };
                queued.Add((NzbDrone.Core.Messaging.Commands.Command)args[0]);
                return new NzbDrone.Core.Messaging.Commands.CommandModel();
            });
            var service = new DeferredCoverDownloadService(h.Deferred, null, queue, h.Subject, LogManager.GetCurrentClassLogger());
            service.HandleAsync(new NzbDrone.Core.MediaFiles.Events.ImportStageProgressEvent(
                NzbDrone.Core.MediaFiles.Events.ImportStage.ImportComplete, "done") { CommandId = 77 });
            if (queueResult != "none") Assert.That(queued, Is.Empty);
            service.Execute(new RetryBookCoversCommand());
            service.Execute(new RetryBookCoversCommand());
            service.Handle(new NzbDrone.Core.Messaging.Events.CommandExecutedEvent(new NzbDrone.Core.Messaging.Commands.CommandModel { Id = 77 }));
            Assert.That(queued, Has.Count.EqualTo(1));
            Assert.That(queued[0], Is.TypeOf<RepairAuthorMediaCoversCommand>());
            Assert.That(attempts, Is.EqualTo(queueResult == "none" ? 1 : 2));
            var authors = InterfaceProxy.Proxy<IAuthorService>((_, _) => new List<Author>());
            new RepairAuthorMediaCoversService(authors, h.Subject, h.DiskProvider, queue, LogManager.GetCurrentClassLogger(), null)
                .Execute((RepairAuthorMediaCoversCommand)queued[0]);
            Assert.That(h.RepairIdReads, Is.EqualTo(1));
            Assert.That(h.Responses.Calls, Is.EqualTo(1), "The skipped startup work now actually downloads");
        }

        [Test]
        public void repair_detects_a_legacy_placeholder_even_when_every_size_exists()
        {
            using var h = new CoverRefreshHarness(false);
            h.SetPhoto("legacy-repair.png", h.Responses.ResponseData);
            h.Refresh();
            var receipt = h.Disk.TextByPath.Keys.Single();
            var json = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[receipt]);
            json["selectedEdition"]["contentHash"] = null;
            h.Disk.TextByPath[receipt] = json.ToString();
            h.Disk.BinaryByPath[h.Original] = Convert.FromBase64String(MascotWebpBase64);
            h.Restart();
            var calls = h.Responses.Calls;
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.EqualTo(calls + 1));
            Assert.That(h.Disk.BinaryByPath[h.Original], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void repair_isolates_id_query_and_page_failures()
        {
            using var h = new CoverRefreshHarness(false);
            h.RepairIdFailure = new IOException("Fixture database unavailable");
            Assert.DoesNotThrow(h.Subject.RepairBookCovers);
            Assert.That(h.Logs.Logs, Has.Some.Contains("could not select IDs"));
            h.RepairIdFailure = null;
            h.RepairBooks = Enumerable.Range(1, 251).Select(id => new Book { Id = id, Editions = new() }).ToList();
            h.BeforeRepairPage = () => { if (h.RepairPageReads == 1) throw new IOException("Fixture page unavailable"); };
            Assert.DoesNotThrow(h.Subject.RepairBookCovers);
            Assert.That(h.RepairPageReads, Is.EqualTo(2));
            Assert.That(h.Logs.Logs, Has.Some.Contains("continuing with the next page"));
        }

        [Test]
        public void failed_new_url_keeps_serving_only_the_same_edition_with_its_old_token()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().GoodreadsEditionId = 13;
            h.Refresh();
            var before = h.LocalUrl();
            h.SetPhoto("same-edition-new.jpg", h.Responses.ResponseData);
            h.Responses.FailureStatus = HttpStatusCode.InternalServerError;
            h.Refresh();
            string Mapped()
            {
                var images = new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, h.Cover.Url) };
                h.Subject.ConvertBookCoversToLocalUrls(new[] { new KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>(h.Book.Id, images) });
                return images[0].Url;
            }
            Assert.That(Mapped(), Is.EqualTo(before));
            h.Book.Editions.Single().BookId = h.Book.Id;
            Assert.That(h.Subject.ReadLocalBookCover(h.Book.Editions.Single()).Ready, Is.False,
                "Showing the old image must not permit embedding it for the new URL");
            h.Book.Editions.Single().GoodreadsEditionId = 999;
            h.Book.Editions.Single().ForeignEditionId = "gr:edition:999";
            h.Refresh();
            Assert.That(Mapped(), Does.Not.Contain("/MediaCover/Books/"), "The writer removes a proven old-edition cover");
        }

        [Test]
        public void forced_first_photo_tries_fallback_after_placeholder()
        {
            using var h = new CoverRefreshHarness(true);
            h.SetPhoto("force-empty-placeholder.png", Convert.FromBase64String(MascotWebpBase64));
            h.Author.Images.Add(new(MediaCoverTypes.Poster, "https://images.example/force-empty-real.png"));
            var real = BuildPngBytes(24, 32, new Rgba32(1, 2, 3));
            h.Responses.ResponseQueue.Enqueue(Convert.FromBase64String(MascotWebpBase64));
            h.Responses.ResponseQueue.Enqueue(real);
            h.Subject.EnsureAuthorCovers(h.Author, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.BinaryByPath.Values, Has.Some.EqualTo(real));
        }

        [Test]
        public void repair_recovers_a_failed_book_cover_without_refetching_complete_covers()
        {
            using var h = new CoverRefreshHarness(false);
            h.SetPhoto("repair-book.png", h.Responses.ResponseData);
            h.Responses.Failure = new WebException("Fixture offline");
            h.Refresh();
            h.Responses.Failure = null;
            var authors = InterfaceProxy.Proxy<IAuthorService>((_, _) => new List<Author>());
            var events = DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>();
            var repair = new RepairAuthorMediaCoversService(authors, h.Subject, h.DiskProvider,
                DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>(), LogManager.GetCurrentClassLogger(), events);
            repair.Execute(new RepairAuthorMediaCoversCommand());
            Assert.That(h.Disk.ExistingPaths, Does.Contain(h.Sized(500)));
            var requests = h.Responses.Calls;
            repair.Execute(new RepairAuthorMediaCoversCommand());
            Assert.That(h.Responses.Calls, Is.EqualTo(requests));
            Assert.That(h.Logs.Logs, Has.Some.Contains("1 candidates, 1 repaired, 0 incomplete"));
        }

        [Test]
        public void repair_respects_library_scope_and_only_the_monitored_editions_cover()
        {
            using var h = new CoverRefreshHarness(false);
            h.LibraryOnly = true;
            h.Book.Editions.Add(new Edition { Id = 99, Monitored = false, Images = new() { new(MediaCoverTypes.Cover, "https://images.example/unmonitored.png") } });
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.Zero);
            h.LibraryIds.Add(h.Book.Id);
            h.Subject.RepairBookCovers();
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            Assert.That(h.Disk.TextByPath.Values.Single(), Does.Contain(h.Cover.Url));
        }

        [Test]
        public void legacy_placeholder_original_is_not_kept_after_a_failed_forced_download()
        {
            using var h = new CoverRefreshHarness(false);
            h.SetPhoto("legacy-book-placeholder.png", h.Responses.ResponseData);
            h.FailedHeight = 250;
            h.Refresh();
            h.FailedHeight = null;
            var receipt = h.Disk.TextByPath.Keys.Single();
            h.Disk.TextByPath[receipt] = new { SelectedEdition = new { CoverUrl = h.Cover.Url, EditionProviderId = "gr:edition:13" } }.ToJson();
            var placeholder = Convert.FromBase64String(MascotWebpBase64);
            h.Disk.BinaryByPath[h.Original] = placeholder;
            h.Restart();
            h.Responses.ResponseData = placeholder;
            h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(h.Original));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(h.Cover.Url), Is.True);
        }

        [Test]
        public void book_placeholder_is_not_kept_or_requested_again()
        {
            using var h = new CoverRefreshHarness(false);
            h.SetPhoto("book-placeholder-once.png", h.Responses.ResponseData);
            h.Refresh();
            var path = h.Sized(500);
            var placeholder = Convert.FromBase64String(MascotWebpBase64);
            var hash = MediaCoverRendition.ComputeContentSha256(placeholder);
            var receipt = h.Disk.TextByPath.Keys.Single();
            var json = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[receipt]);
            json["selectedEdition"]["contentHash"] = hash;
            h.Disk.TextByPath[receipt] = json.ToString();
            h.Disk.BinaryByPath[path] = placeholder;
            h.Restart();
            h.Responses.ResponseData = placeholder;
            h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(path));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(h.Cover.Url), Is.True);
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
        }

        [Test]
        public void cached_placeholder_cannot_be_copied_to_another_book()
        {
            using var h = new CoverRefreshHarness(false);
            h.SetPhoto("reusable-placeholder.png", h.Responses.ResponseData);
            h.FailedHeight = 250; // Keep the original available for the reuse path.
            h.Refresh();
            h.FailedHeight = null;
            Assert.That(h.Disk.ExistingPaths, Does.Contain(h.Original));
            var placeholder = Convert.FromBase64String(MascotWebpBase64);
            var receipt = h.Disk.TextByPath.Keys.Single();
            var json = Newtonsoft.Json.Linq.JObject.Parse(h.Disk.TextByPath[receipt]);
            json["selectedEdition"]["contentHash"] = MediaCoverRendition.ComputeContentSha256(placeholder);
            h.Disk.TextByPath[receipt] = json.ToString();
            // Re-prime the URL reuse cache with a legacy receipt before the placeholder
            // verdict is loaded. The source's bytes and receipt then identify a silhouette.
            foreach (var path in h.Disk.BinaryByPath.Keys.ToArray()) h.Disk.BinaryByPath[path] = placeholder;
            // The cache's receipt must agree with the stored file, not the earlier image.
            var cache = (System.Collections.IDictionary)typeof(MediaCoverService).GetField("_bookCoverMetadataByBookId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(h.Subject);
            cache.Clear();
            var other = new Book { Id = 99, Title = "Other book", Editions = h.Book.Editions };
            h.Responses.ResponseData = BuildPngBytes(24, 32, new Rgba32(90, 30, 20));
            h.Subject.EnsureBookCovers(other);
            Assert.That(h.Responses.Calls, Is.EqualTo(2), "A placeholder reuse entry must fall through to a real fetch");
            var destination = h.Subject.GetCoverPath(99, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png", 500);
            Assert.That(h.Disk.BinaryByPath[destination], Is.EqualTo(h.Responses.ResponseData));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void receipt_io_failure_retries_but_corruption_is_cached(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var receipt = h.Disk.TextByPath.Keys.Single(p => p.EndsWith(authorCover
                ? MediaCoverRendition.GetAuthorCoverIdentityFileName(MediaCoverTypes.Poster) : "cover-metadata.json"));
            var valid = h.Disk.TextByPath[receipt];
            var reads = 0;
            h.Restart();
            h.Disk.BeforeReadText = p =>
            {
                if (p == receipt && ++reads == 1) throw new IOException("Fixture temporary receipt lock");
            };
            h.LocalUrl(); h.LocalUrl();
            Assert.That(reads, Is.EqualTo(2), "A temporary read failure must not be cached");
            h.Restart(); reads = 0; h.Disk.TextByPath[receipt] = "broken";
            h.Disk.BeforeReadText = p => { if (p == receipt) reads++; };
            h.LocalUrl(); h.LocalUrl();
            Assert.That(reads, Is.EqualTo(1), "Persistent corruption is read once");
            h.Disk.TextByPath[receipt] = valid;
        }

        [Test]
        public void forced_placeholder_defers_existing_placeholder_removal_to_ordinary_refresh()
        {
            using var h = new CoverRefreshHarness(true);
            h.SetPhoto("legacy-placeholder.png", h.Responses.ResponseData);
            var placeholder = Convert.FromBase64String(MascotWebpBase64);
            h.Disk.BinaryByPath[h.Original] = placeholder; h.Disk.ExistingPaths.Add(h.Original);
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), MediaCoverRendition.GetAuthorCoverIdentityFileName(MediaCoverTypes.Poster));
            h.Disk.TextByPath[identity] = "content-sha256-v1\n" + MediaCoverRendition.ComputeContentSha256(placeholder) + "\n" + h.Cover.Url;
            h.Disk.ExistingPaths.Add(identity);
            h.Responses.ResponseData = placeholder;
            var original = h.Original;
            h.Subject.EnsureAuthorCovers(h.Author, true);
            Assert.That(h.Disk.ExistingPaths, Does.Contain(original));
            Assert.That(h.Author.Images, Is.Not.Empty);
            h.Refresh();
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(original));
            Assert.That(h.Author.Images, Is.Empty);
            Assert.That(h.Logs.Logs, Has.Some.Contains("Known placeholder receipt"));
        }

        [Test]
        public void placeholder_book_candidate_tries_next_cover_and_never_keeps_another_editions_pixels()
        {
            using var h = new CoverRefreshHarness(false);
            h.Book.Editions.Single().GoodreadsEditionId = 13;
            h.Refresh();
            h.Book.Editions.Single().GoodreadsEditionId = 99;
            h.Book.Editions.Single().Id = 99;
            h.Book.Editions.Single().ForeignEditionId = "gr:99";
            h.SetPhoto("placeholder-next-edition.png", Convert.FromBase64String(MascotWebpBase64));
            h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(h.Sized(500)), "A different edition cannot keep the old cover");
            h.Book.Editions.Single().Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/fallback.png"));
            h.Responses.FailureForRequest = request =>
            {
                h.Responses.ResponseData = request.Url.FullUri.EndsWith("fallback.png")
                    ? BuildPngBytes(24, 32, new Rgba32(200, 10, 20)) : Convert.FromBase64String(MascotWebpBase64);
                return null;
            };
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(3), "Registered placeholder URL is not downloaded again");
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void stored_complete_book_cover_does_not_request_the_host()
        {
            using var h = new CoverRefreshHarness(false); h.Refresh();
            h.Responses.FailureForRequest = _ => throw new AssertionException("No HTTP call for stored complete cover");
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deferred_switch_removes_only_a_proven_different_editions_cover(bool sameEdition)
        {
            using var h = new CoverRefreshHarness(false);
            h.Refresh();
            var calls = h.Responses.Calls;
            var edition = h.Book.Editions.Single();
            if (!sameEdition) edition.ForeignEditionId = "gr:edition:14";
            edition.Images.Single().Url = "https://images.example/new.png";
            var deferred = (DeferredCoverProxy)(object)h.Deferred;
            deferred.ShouldDefer = true;
            h.Subject.EnsureBookCovers(h.Book);
            Assert.That(h.Responses.Calls, Is.EqualTo(calls));
            Assert.That(h.Disk.ExistingPaths.Contains(h.Original), Is.EqualTo(sameEdition));
            Assert.That(deferred.MarkedBookIds, Does.Contain(h.Book.Id));
            deferred.ShouldDefer = false;
            h.Subject.EnsureBookCovers(h.Book);
            Assert.That(h.Responses.Calls, Is.EqualTo(calls + 1));
            Assert.That(h.Disk.ExistingPaths, Does.Contain(h.Original));
        }

        [Test]
        public void forced_book_download_bypasses_import_deferral()
        {
            using var h = new CoverRefreshHarness(false);
            h.Refresh();
            var deferred = (DeferredCoverProxy)(object)h.Deferred;
            deferred.MarkedBookIds.Clear();
            deferred.ShouldDefer = true;
            h.Responses.ResponseData = BuildPngBytes(24, 32, new Rgba32(200, 20, 10));
            h.Subject.EnsureBookCovers(h.Book, true);
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(deferred.MarkedBookIds, Is.Empty);
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public async Task previously_certified_html_variant_is_downloaded_again()
        {
            using var h = new CoverRefreshHarness(true);
            var first = await h.Subject.EnsureAuthorImage(h.Author, h.Cover);
            Assert.That(first.State, Is.EqualTo("downloaded"));
            var html = Encoding.UTF8.GetBytes("<html>old CDN failure</html>");
            h.Disk.BinaryByPath[first.Path] = html;
            h.Disk.TextByPath[first.Path + ".sha256"] = MediaCoverRendition.OriginalImageVerificationPrefix + MediaCoverRendition.ComputeContentSha256(html);
            var next = await h.Subject.EnsureAuthorImage(h.Author, h.Cover);
            Assert.That(next.State, Is.EqualTo("downloaded"));
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.BinaryByPath[next.Path], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void deferred_book_request_is_queued_even_while_an_older_cover_is_in_flight()
        {
            using var h = new CoverRefreshHarness(false);
            var deferred = (DeferredCoverProxy)(object)h.Deferred;
            h.Disk.BeforeWriteText = _ =>
            {
                h.Disk.BeforeWriteText = null;
                deferred.MarkedBookIds.Clear();
                deferred.ShouldDefer = true;
                h.Subject.EnsureBookCovers(h.Book);
            };
            h.Refresh();
            Assert.That(deferred.MarkedBookIds, Is.EqualTo(new[] { h.Book.Id }));
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
        }

        [TestCase(false, false, 0)]
        [TestCase(true, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 1)]
        public void repair_publishes_once_only_when_a_photo_or_image_list_changed(bool downloaded, bool placeholder, int expected)
        {
            var service = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authors = (AuthorServiceProxy)(object)service;
            authors.DetachedRemoval = true;
            var author = CreateAuthor(91);
            if (placeholder)
            {
                const string url = "https://images.example/repair-placeholder.png";
                MediaCoverRendition.RegisterKnownPlaceholderImage(url, KnownPlaceholderJpegHash);
                author.Images.Add(new(MediaCoverTypes.Poster, url));
            }
            authors.Authors.Add(author);
            var events = DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>();
            var mapper = new CoverMapperStub { Changed = downloaded };
            new RepairAuthorMediaCoversService(service, mapper, DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>(), LogManager.GetCurrentClassLogger(), events)
                .Execute(new RepairAuthorMediaCoversCommand());
            var published = ((EventAggregatorProxy)(object)events).Published.OfType<MediaCoversUpdatedEvent>().ToList();
            Assert.That(published.Count, Is.EqualTo(expected));
            foreach (var update in published)
                Assert.That(update.Author.Images.Any(cover => MediaCoverRendition.IsKnownPlaceholderImageUrl(cover.Url)), Is.False);
        }

        [Test]
        public void failed_author_publication_releases_its_inflight_slot()
        {
            using var h = new CoverRefreshHarness(true);
            h.Disk.RefusePublication = true;
            h.Refresh();
            h.Disk.RefusePublication = false;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(2));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void rejected_candidate_then_local_write_failure_keeps_the_previous_photo()
        {
            using var h = new CoverRefreshHarness(true);
            h.Refresh();
            var small = h.Sized(250);
            var large = h.Sized(500);
            var bytes = h.Disk.BinaryByPath[small].ToArray();
            h.SetPhoto("candidate.png", BuildPngBytes(24, 32, new Rgba32(150, 40, 20)));
            h.Author.Images.Add(new(MediaCoverTypes.Poster, "https://images.example/second-candidate.png"));
            h.Responses.ResponseQueue.Enqueue(Convert.FromBase64String(MascotWebpBase64));
            h.Responses.ResponseQueue.Enqueue(h.Responses.ResponseData);
            h.Disk.RefusePublication = true;
            h.Refresh();
            Assert.That(h.Disk.BinaryByPath[small], Is.EqualTo(bytes));
            Assert.That(h.Disk.BinaryByPath[large], Is.EqualTo(bytes));
        }

        [Test]
        public void locked_abandoned_temporary_does_not_stop_cleanup_or_other_photo_types()
        {
            using var h = new CoverRefreshHarness(true);
            var folder = Path.GetDirectoryName(h.Original);
            var locked = Path.Combine(folder, ".write-prior-locked.png");
            var removable = Path.Combine(folder, ".write-prior-removable.png");
            h.Disk.ExistingPaths.UnionWith(new[] { locked, removable });
            h.Disk.BeforeDelete = path => { if (path == locked) throw new IOException("Fixture locked orphan"); };
            h.Author.Images.Add(new(MediaCoverTypes.Banner, "https://images.example/banner.png"));
            Assert.DoesNotThrow(h.Refresh);
            Assert.That(h.Disk.ExistingPaths, Does.Contain(locked).And.Not.Contain(removable));
            foreach (var type in new[] { MediaCoverTypes.Poster, MediaCoverTypes.Banner })
            {
                var identity = Path.Combine(folder, MediaCoverRendition.GetAuthorCoverIdentityFileName(type));
                Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(h.Disk.TextByPath[identity], out _, out _), Is.True);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void failed_identity_write_does_not_resize_and_revert_checks_the_kept_original(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            var first = h.Responses.ResponseData.ToArray();
            h.Refresh();
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), "poster.url");
            if (!authorCover) identity = Path.Combine(Path.GetDirectoryName(h.Original), "cover-metadata.json");
            var resizes = h.Resizes;
            h.SetPhoto("replacement.png", BuildPngBytes(24, 32, new Rgba32(150, 40, 20)));
            var alternative = new NzbDrone.Core.MediaCover.MediaCover(h.Cover.CoverType, "https://images.example/alternative.png");
            if (authorCover) h.Author.Images.Add(alternative);
            else h.Book.Editions.Single().Images.Add(alternative);
            var attempted = h.Responses.Calls;
            h.Disk.BeforeWriteText = path => { if (path == identity || (!authorCover && Path.GetExtension(path) == ".json")) throw new IOException("Fixture identity write refused"); };
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(attempted + 1), "A local receipt failure must not fetch the next provider");
            Assert.That(h.Resizes, Is.EqualTo(resizes), "Do not derive files before their identity was saved");
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(identity).And.Not.Contain(h.Original),
                "Neither an old receipt nor an unrecorded replacement may remain");
            h.Disk.BeforeWriteText = null;
            h.Restart(); // Forget cached identities, as after a process crash.
            h.SetPhoto("first.png", first);
            var requests = h.Responses.Calls;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.GreaterThan(requests));
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(first));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(first));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void locked_previous_size_keeps_old_pixels_and_refuses_new_receipt(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var old = h.Disk.BinaryByPath[h.Sized(500)].ToArray();
            var replacement = BuildPngBytes(24, 32, new Rgba32(150, 40, 20));
            h.SetPhoto("replacement.png", replacement);
            var locked = h.Sized(500);
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), authorCover ? "poster.url" : "cover-metadata.json");
            h.Disk.BeforeDelete = path => { if (path == locked) throw new IOException("Fixture locked rendition"); };
            var resizes = h.Resizes;
            h.Refresh();
            Assert.That(h.Disk.BinaryByPath[locked], Is.EqualTo(old));
            Assert.That(h.Resizes, Is.EqualTo(resizes), "Never generate a mixed family when cleanup fails");
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(identity).And.Not.Contain(h.Original));
            h.Disk.BeforeDelete = null;
            h.Restart();
            var calls = h.Responses.Calls;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(calls + 1));
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(replacement));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(replacement));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void cover_download_uses_one_response_and_does_not_rewrite_file_times(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Responses.LastModified = new DateTime(2020, 6, 12, 12, 34, 56, DateTimeKind.Utc);
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(1));
            Assert.That(h.Disk.GetCallCount(nameof(IDiskProvider.FileSetLastWriteTime)), Is.Zero);
        }

        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        public void interrupted_publication_never_reuses_new_pixels_as_the_previous_photo(bool authorCover, int step)
        {
            using var h = new CoverRefreshHarness(authorCover);
            var first = h.Responses.ResponseData.ToArray();
            h.Refresh();
            h.SetPhoto("replacement.png", BuildPngBytes(24, 32, new Rgba32(150, 40, 20)));
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), authorCover ? "poster.url" : "cover-metadata.json");
            Action restore = null;
            void Snapshot(string path)
            {
                var expectedPath = step == 1 ? h.Original : identity;
                if (path != expectedPath && !(step == 2 && !authorCover && Path.GetExtension(path) == ".json")) return;
                var files = h.Disk.ExistingPaths.ToArray();
                var text = h.Disk.TextByPath.ToDictionary(p => p.Key, p => p.Value);
                var bytes = h.Disk.BinaryByPath.ToDictionary(p => p.Key, p => p.Value.ToArray());
                restore = () =>
                {
                    h.Disk.ExistingPaths.Clear(); h.Disk.ExistingPaths.UnionWith(files);
                    h.Disk.TextByPath.Clear(); foreach (var p in text) h.Disk.TextByPath[p.Key] = p.Value;
                    h.Disk.BinaryByPath.Clear(); foreach (var p in bytes) h.Disk.BinaryByPath[p.Key] = p.Value;
                };
            }
            if (step == 1) h.Disk.AfterPublication = Snapshot;
            if (step == 2) h.Disk.BeforeWriteText = Snapshot;
            if (step == 3)
            {
                if (authorCover) h.Disk.AfterWriteText = Snapshot;
                else h.Disk.AfterPublication = Snapshot;
            }
            h.Refresh();
            Assert.That(restore, Is.Not.Null);
            restore(); // Restore the exact on-disk state at the simulated process exit.
            h.Disk.AfterPublication = h.Disk.BeforeWriteText = h.Disk.AfterWriteText = null;
            Assert.That(h.Disk.ExistingPaths.Contains(identity), Is.EqualTo(step == 3));
            foreach (var height in new[] { 250, 500 })
            {
                if (step == 1) Assert.That(h.Disk.BinaryByPath[h.Sized(height)], Is.EqualTo(first));
                else Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(h.Sized(height)));
            }
            h.Restart();
            h.SetPhoto("first.png", first);
            var requests = h.Responses.Calls;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests + 1), "Reverting must fetch A, never resize the interrupted B original");
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(first));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(first));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void incomplete_cover_with_wrong_original_hash_is_fetched_again(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.FailedHeight = 500;
            h.Refresh();
            h.Disk.BinaryByPath[h.Original] = BuildPngBytes(24, 32, new Rgba32(150, 40, 20));
            h.FailedHeight = null;
            h.Restart();
            var requests = h.Responses.Calls;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests + 1));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(h.Responses.ResponseData));
        }

        [Test]
        public void successful_author_identity_is_persisted_before_any_resize()
        {
            using var h = new CoverRefreshHarness(true);
            var identity = Path.Combine(Path.GetDirectoryName(h.Original), "poster.url");
            var observed = new List<string>();
            h.BeforeResize = () => observed.Add(h.Disk.TextByPath.GetValueOrDefault(identity));
            h.Refresh();
            Assert.That(observed, Has.Count.EqualTo(2));
            foreach (var value in observed)
            {
                Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(value, out var url, out var hash), Is.True);
                Assert.That(url, Is.EqualTo(h.Cover.Url));
                Assert.That(hash, Is.EqualTo(MediaCoverRendition.ComputeContentSha256(h.Responses.ResponseData)));
            }
        }

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void partial_resize_records_new_original_and_repairs_without_download(bool authorCover, bool replacing)
        {
            using var h = new CoverRefreshHarness(authorCover);
            if (replacing) h.Refresh();
            var bytes = BuildPngBytes(24, 32, new Rgba32(200, 100, 20));
            h.SetPhoto("second.png", bytes);
            h.FailedHeight = 500;
            h.Refresh();
            var requests = h.Responses.Calls;
            var resizes = h.Resizes;
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(h.Sized(500)), "An old size cannot stand in for the new photo");
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(bytes));
            Assert.That(h.LocalUrl(), Does.StartWith(authorCover ? "/MediaCover/12/" : "/MediaCover/Books/12/"));
            var mapper = new MediaCoverMapper(h.AppFolder, h.DiskProvider, LogManager.GetCurrentClassLogger());
            Assert.That(mapper.Map((authorCover ? "/MediaCover/12/poster-500" : "/MediaCover/Books/12/cover-500") + ".png"), Is.EqualTo(h.Original));

            h.FailedHeight = null;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests), "Repair uses the recorded original without even a header request");
            Assert.That(h.Resizes, Is.EqualTo(resizes + 1));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(bytes));
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests));
            Assert.That(h.Resizes, Is.EqualTo(resizes + 1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void photo_revert_after_partial_resize_never_labels_new_pixels_as_old(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            var oldBytes = h.Responses.ResponseData;
            h.Refresh();
            var oldUrl = h.LocalUrl();
            h.SetPhoto("second.png", BuildPngBytes(24, 32, new Rgba32(200, 100, 20)));
            h.FailedHeight = 500;
            h.Refresh();
            Assert.That(h.LocalUrl(), Is.Not.EqualTo(oldUrl));
            h.SetPhoto("first.png", oldBytes);
            h.FailedHeight = null;
            h.Refresh();
            Assert.That(h.Disk.BinaryByPath[h.Sized(250)], Is.EqualTo(oldBytes));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(oldBytes));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void new_avif_discards_previous_sizes_and_keeps_recorded_original(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            h.Refresh();
            var oldSmall = h.Sized(250);
            var oldLarge = h.Sized(500);
            var avif = Convert.FromBase64String("AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAcAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAQAAAAGAAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAkbWRhdAoGGA0+7YBAMhIYAAAAUAAAAAINg0uQg2xym8g=");
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(avif, "image/avif"));
            Assert.Throws<UnknownImageFormatException>(() => Image.Load(avif));
            h.SetPhoto("second.avif", avif);
            h.Undecodable = true;
            h.Refresh();
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(oldSmall).And.Not.Contain(oldLarge));
            Assert.That(h.LocalUrl(), Does.Contain(".avif?v="));
            var requests = h.Responses.Calls;
            h.Refresh();
            Assert.That(h.Responses.Calls, Is.EqualTo(requests));
            Assert.That(h.Disk.BinaryByPath[h.Original], Is.EqualTo(avif));
        }

        [Test]
        public void edition_switch_back_does_not_reuse_another_editions_bytes()
        {
            using var h = new CoverRefreshHarness(false);
            var first = h.Responses.ResponseData;
            h.Refresh();
            h.Book.Editions.Single().ForeignEditionId = "gr:edition:14";
            h.SetPhoto("second.png", BuildPngBytes(24, 32, new Rgba32(200, 100, 20)));
            h.Refresh();
            var requests = h.Responses.Calls;
            h.Book.Editions.Single().ForeignEditionId = "gr:edition:13";
            h.SetPhoto("first.png", first);
            var oldSizeSeen = false;
            h.BeforeResize = () => oldSizeSeen |= h.Disk.ExistingPaths.Contains(h.Sized(500));
            h.Refresh();
            Assert.That(oldSizeSeen, Is.False, "Old sizes are removed before generating replacements");
            Assert.That(h.Responses.Calls, Is.EqualTo(requests + 1));
            Assert.That(h.Disk.BinaryByPath[h.Original], Is.EqualTo(first));
            Assert.That(h.Disk.BinaryByPath[h.Sized(500)], Is.EqualTo(first));
        }

        [Test]
        public void cross_book_reuse_requires_the_source_to_still_own_the_requested_image()
        {
            using var h = new CoverRefreshHarness(false);
            var first = h.Responses.ResponseData;
            h.Refresh();
            var other = new Book { Id = 15, Author = h.Author, Editions = new List<Edition>
            {
                new() { Id = 16, ForeignEditionId = "gr:edition:16", Monitored = true,
                    Images = new() { new(MediaCoverTypes.Cover, h.Cover.Url) } }
            } };
            var requests = h.Responses.Calls;
            h.Subject.EnsureBookCovers(other);
            Assert.That(h.Responses.Calls, Is.EqualTo(requests), "An idle source with the correct receipt is reusable");
            // The cache now points to the second book's mutable original.
            other.Editions.Single().Images = new() { new(MediaCoverTypes.Cover, "https://images.example/changed.png") };
            h.Responses.ResponseData = BuildPngBytes(24, 32, new Rgba32(200, 100, 20));
            h.Subject.EnsureBookCovers(other);
            var third = new Book { Id = 18, Author = h.Author, Editions = new List<Edition>
            {
                new() { Id = 19, ForeignEditionId = "gr:edition:19", Monitored = true,
                    Images = new() { new(MediaCoverTypes.Cover, h.Cover.Url) } }
            } };
            h.Responses.ResponseData = first;
            requests = h.Responses.Calls;
            h.Subject.EnsureBookCovers(third);
            Assert.That(h.Responses.Calls, Is.EqualTo(requests + 1));
            var path = h.Subject.GetCoverPath(third.Id, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
            Assert.That(h.Disk.BinaryByPath[path], Is.EqualTo(first));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void cleanup_removes_prior_run_temporaries_but_never_active_writes(bool authorCover)
        {
            using var h = new CoverRefreshHarness(authorCover);
            var folder = Path.GetDirectoryName(h.Original);
            var orphan = Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N") + ".png");
            var active = Path.Combine(folder, DiskProviderBase.CurrentTemporaryWritePrefix + Guid.NewGuid().ToString("N") + ".png");
            var unrelated = Path.Combine(folder, "unrelated.txt");
            h.Disk.ExistingPaths.UnionWith(new[] { orphan, active, unrelated });
            h.Disk.BinaryByPath[orphan] = h.Responses.ResponseData;
            h.Disk.BinaryByPath[active] = h.Responses.ResponseData;
            h.Refresh();
            Assert.That(h.Disk.ExistingPaths, Does.Not.Contain(orphan).And.Contain(active).And.Contain(unrelated));
            var mapper = new MediaCoverMapper(h.AppFolder, h.DiskProvider, LogManager.GetCurrentClassLogger());
            foreach (var temporary in new[] { orphan, active })
            {
                var request = (authorCover ? "/MediaCover/12/" : "/MediaCover/Books/12/") + Path.GetFileName(temporary);
                var result = mapper.GetResponse(request);
                if (result is FileStreamResult image) image.FileStream.Dispose();
                Assert.That(result, Is.Null, "Temporary images must not be served, even if their path is known");
                Assert.That(mapper.Map(request.Replace(".write-", ".WRITE-")), Is.Null, "Windows paths are case-insensitive");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_download_publishes_complete_bytes_and_keeps_old_photo_on_write_failure(bool fail)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-author-write-" + Guid.NewGuid().ToString("N"));
            var original = Path.Combine(folder, "MediaCover", "12", "poster.png");
            Directory.CreateDirectory(Path.GetDirectoryName(original));
            try
            {
                using var logs = new LogFactory();
                var oldBytes = BuildPngBytes(12, 16, new Rgba32(10, 20, 30));
                var newBytes = BuildPngBytes(24, 32, new Rgba32(20, 100, 200));
                File.WriteAllBytes(original, oldBytes);
                var realDisk = RealDiskProvider.Create(logs.GetLogger("AuthorWriteFixture"));
                byte[] duringWrite = null;
                string writtenPath = null;
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.SaveStream))
                    {
                        writtenPath = (string)args[1];
                        File.WriteAllBytes(writtenPath, fail ? newBytes.Take(newBytes.Length / 2).ToArray() : newBytes);
                        duringWrite = File.ReadAllBytes(original);
                        if (fail)
                        {
                            throw new IOException("Interrupted author write");
                        }

                        return null;
                    }

                    return method.Invoke(realDisk, args);
                });
                var http = InterfaceProxy.Proxy<IHttpClient>((_, args) =>
                    new HttpResponse((HttpRequest)args[0], new HttpHeader(), newBytes));
                var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
                ((AppFolderProxy)(object)appFolder).AppDataFolder = folder;
                // Keep the original so this test can inspect it after the normal refresh path.
                var resizer = InterfaceProxy.Proxy<IImageResizer>((_, _) => throw new IOException("Fixture keeps original"));
                var subject = new MediaCoverService(null, resizer, null, http, disk, appFolder, null, null, null, null, logs.GetLogger("AuthorWriteFixture"), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
                subject.EnsureAuthorCovers(new Author { Id = 12, Name = "Photo write", Images = new()
                {
                    new(MediaCoverTypes.Poster, "https://images.example/photo.png")
                } });

                Assert.That(writtenPath, Is.Not.Null.And.Not.EqualTo(original), "Never open the visible original for writing");
                Assert.That(duringWrite, Is.EqualTo(oldBytes));
                Assert.That(File.ReadAllBytes(original), Is.EqualTo(fail ? oldBytes : newBytes));
                Assert.That(Directory.GetFiles(folder, ".write-*", SearchOption.AllDirectories), Is.Empty);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void resizer_publishes_complete_bytes_and_keeps_previous_size_on_failure(bool fail)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-resize-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var source = Path.Combine(folder, "poster.png");
                var destination = Path.Combine(folder, "poster-250.png");
                var oldBytes = BuildPngBytes(12, 16, new Rgba32(10, 20, 30));
                File.WriteAllBytes(source, BuildPngBytes(24, 32, new Rgba32(20, 100, 200)));
                File.WriteAllBytes(destination, oldBytes);
                var realDisk = RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
                byte[] beforeReplace = null;
                var writes = 0;
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.WriteFileAtomically))
                    {
                        writes++;
                        realDisk.WriteFileAtomically((string)args[0], temporaryPath =>
                        {
                            ((Action<string>)args[1])(temporaryPath);
                            beforeReplace = File.ReadAllBytes(destination);
                            if (fail)
                            {
                                throw new IOException("Interrupted resize write");
                            }
                        });
                        return null;
                    }

                    return method.Invoke(realDisk, args);
                });
                TestDelegate resize = () => new ImageResizer(disk).Resize(source, destination, 10);
                if (fail)
                {
                    Assert.That(resize, Throws.TypeOf<IOException>().With.Message.EqualTo("Interrupted resize write"));
                    Assert.That(File.ReadAllBytes(destination), Is.EqualTo(oldBytes));
                }
                else
                {
                    Assert.DoesNotThrow(resize);
                    Assert.That(Image.Identify(destination)?.Height, Is.EqualTo(10));
                }

                Assert.That(writes, Is.EqualTo(1));
                Assert.That(beforeReplace, Is.EqualTo(oldBytes));
                Assert.That(Directory.GetFiles(folder), Is.EquivalentTo(new[] { source, destination }));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void unreadable_resize_source_does_not_delete_previous_rendition()
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-resize-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var source = Path.Combine(folder, "poster.png");
                var destination = Path.Combine(folder, "poster-250.png");
                var oldBytes = BuildPngBytes(12, 16, new Rgba32(10, 20, 30));
                File.WriteAllText(source, "broken source");
                File.WriteAllBytes(destination, oldBytes);
                var resizer = new ImageResizer(RealDiskProvider.Create(LogManager.GetCurrentClassLogger()));
                Assert.Throws<UnknownImageFormatException>(() => resizer.Resize(source, destination, 10));
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(oldBytes));
                Assert.That(Directory.GetFiles(folder), Is.EquivalentTo(new[] { source, destination }));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestCase(".png", "PNG")]
        [TestCase(".jpeg", "JPEG")]
        [TestCase(".jfif", "JPEG")]
        [TestCase(".webp", "WEBP")]
        [TestCase(".gif", "GIF")]
        public void image_resizer_should_preserve_the_destination_file_format(string extension, string expectedFormat)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-image-resizer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "source.png");
            var destination = Path.Combine(folder, "resized" + extension);

            try
            {
                using (var image = new Image<Rgba32>(20, 40))
                {
                    image.Save(source, new PngEncoder());
                }

                new ImageResizer(RealDiskProvider.Create(LogManager.GetCurrentClassLogger())).Resize(source, destination, 10);

                Assert.That(Image.DetectFormat(destination)?.Name, Is.EqualTo(expectedFormat));
                Assert.That(Image.Identify(destination)?.Height, Is.EqualTo(10));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void missing_local_rendition_should_keep_the_remote_proxy_fallback()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/author.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            Assert.That(cover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
        }

        [Test]
        public void display_serves_the_recorded_cover_while_url_replacement_is_pending()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"selectedEdition\":{\"coverUrl\":\"https://images.example/old.jpg\"}}";
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/new.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { cover });

            Assert.That(cover.Url, Does.StartWith("/MediaCover/Books/12/cover.jpg?v="));
            Assert.That(proxy.ProxyCalls, Is.Zero);
        }

        [Test]
        public void missing_book_cover_metadata_should_be_negative_cached_without_reprobing_disk()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var firstCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { firstCover });
            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { secondCover });

            Assert.That(firstCover.Url, Does.StartWith("/MediaCoverProxy/book/12/current.jpg?v="));
            Assert.That(secondCover.Url, Is.EqualTo(firstCover.Url));
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.That(fileExistsCalls, Is.EqualTo(1));
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.Zero);
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.Zero);
        }

        [Test]
        public void lifecycle_verified_book_cover_should_map_locally_without_rechecking_disk()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            var downloadedAt = new DateTime(2026, 8, 7, 1, 2, 3, DateTimeKind.Utc);
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                $"{{\"selectedEdition\":{{\"coverUrl\":\"https://images.example/current.jpg\",\"downloadedAt\":\"{downloadedAt:O}\"}}}}";
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { cover });

            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var getFileSizeCalls = disk.GetCallCount(nameof(IDiskProvider.GetFileSize));
            var readAllTextCalls = disk.GetCallCount(nameof(IDiskProvider.ReadAllText));
            var fileGetLastWriteCalls = disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { secondCover });

            Assert.That(cover.Url, Is.EqualTo($"/MediaCover/Books/12/cover.jpg?v={downloadedAt.Ticks}"));
            Assert.That(secondCover.Url, Is.EqualTo(cover.Url));
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.GetFileSize)), Is.EqualTo(getFileSizeCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.EqualTo(readAllTextCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.EqualTo(fileGetLastWriteCalls));
            });
        }

        [Test]
        public void repair_should_skip_honest_local_authors_and_process_every_missing_or_unproven_author_in_one_pass()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author>
            {
                CreateAuthor(1),
                CreateAuthor(2),
                CreateAuthor(3),
                CreateAuthor(4)
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-250.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-500.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(
                "https://images.example/author.jpg",
                VerifiedRealImageHash);
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "4", "poster-250.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "4", "poster-500.jpg"));
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var queue = (CommandQueueProxy)(object)commandQueue;
            var coverMapper = new CoverMapperStub { DiskProvider = diskProvider };
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger(), DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.EqualTo(new[] { 2, 3, 4 }));
            Assert.That(queue.Commands, Is.Empty);
        }

        [Test]
        public void repair_should_accept_a_local_fallback_provider_with_a_different_extension()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author>
            {
                new()
                {
                    Id = 1,
                    Name = "Author 1",
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Poster, "https://images.example/preferred.jpg"),
                        new(MediaCoverTypes.Poster, "https://images.example/fallback.png")
                    }
                }
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-250.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-500.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(
                "https://images.example/fallback.png",
                VerifiedRealImageHash);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var coverMapper = new CoverMapperStub { DiskProvider = diskProvider };
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger(), DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.Empty);
        }

        [Test]
        public void repair_should_remove_stored_placeholder_and_rebuild_from_real_fallback()
        {
            const string placeholder = "https://assets.hardcover.app/author/900004/provider-default.jpg";
            const string realPhoto = "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/authors/1534005630i/900004._UY200_.jpg";
            Assert.That(MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, KnownPlaceholderJpegHash), Is.True);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authors = (AuthorServiceProxy)(object)authorService;
            authors.Authors = new List<Author>
            {
                new()
                {
                    Id = 900004,
                    Name = "Fallback Author",
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Poster, placeholder),
                        new(MediaCoverTypes.Poster, realPhoto)
                    }
                }
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var coverMapper = new CoverMapperStub { DiskProvider = diskProvider };
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger(), DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.EqualTo(new[] { 900004 }));
            Assert.That(authors.UpdatedAuthors, Has.Count.EqualTo(1));
            Assert.That(authors.UpdatedAuthors.Single().Images.Select(image => image.Url), Is.EqualTo(new[] { realPhoto }));
        }

        [Test]
        public void repair_uses_current_images_and_only_removes_verified_placeholders()
        {
            var service = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authors = (AuthorServiceProxy)(object)service;
            var old = CreateAuthor(91);
            authors.Authors.Add(old);
            var current = CreateAuthor(91);
            current.Monitored = false;
            const string placeholder = "https://images.example/legacy-placeholder.png";
            MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, KnownPlaceholderJpegHash);
            current.Images = new() { new(MediaCoverTypes.Poster, "https://images.example/updated.png"), new(MediaCoverTypes.Poster, placeholder) };
            authors.BeforeGetExisting = () => authors.Authors = new() { current };
            var mapper = new CoverMapperStub();
            new RepairAuthorMediaCoversService(service, mapper, DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>(), LogManager.GetCurrentClassLogger(), DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>())
                .Execute(new RepairAuthorMediaCoversCommand());
            Assert.That(mapper.EnsuredAuthorIds, Is.EqualTo(new[] { 91 }));
            Assert.That(authors.Authors.Single().Monitored, Is.False);
            Assert.That(authors.Authors.Single().Images.Single().Url, Is.EqualTo("https://images.example/updated.png"));
            Assert.That(authors.UpdatedAuthors, Has.Count.EqualTo(1));
        }

        [Test]
        public void repair_should_be_queued_immediately_after_startup()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var queue = (CommandQueueProxy)(object)commandQueue;
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                new CoverMapperStub(),
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger(), DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>());

            subject.Handle(new ApplicationStartedEvent());

            Assert.That(queue.Commands, Has.Count.EqualTo(1));
        }

        [Test]
        public void author_refresh_should_reconcile_every_child_book_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.Books = Enumerable.Range(1, 3)
                .Select(id => new Book { Id = id, Title = $"Book {id}" })
                .ToList();
            var deferredCoverService = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var deferred = (DeferredCoverProxy)(object)deferredCoverService;
            deferred.ShouldDefer = true;
            var eventAggregator = DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: bookService,
                httpClient: null,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                configService: null,
                configFileProvider: null,
                eventAggregator: eventAggregator,
                deferredCoverService: deferredCoverService,
                logger: LogManager.GetCurrentClassLogger(), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author
            {
                Id = 42,
                Name = "Cover Lifecycle Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
            };

            subject.HandleAsync(new NzbDrone.Core.Books.Events.AuthorRefreshCompleteEvent(author));

            Assert.That(books.RequestedAuthorIds, Is.EqualTo(new[] { 42 }));
            Assert.That(deferred.MarkedBookIds, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        private static Author CreateAuthor(int id)
        {
            return new Author
            {
                Id = id,
                Name = $"Author {id}",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/author.jpg")
                }
            };
        }

        internal static byte[] BuildPngBytes(int width, int height, Rgba32 color)
        {
            using var image = new Image<Rgba32>(width, height, color);
            using var stream = new MemoryStream();
            image.Save(stream, new PngEncoder());
            return stream.ToArray();
        }
    }
}
