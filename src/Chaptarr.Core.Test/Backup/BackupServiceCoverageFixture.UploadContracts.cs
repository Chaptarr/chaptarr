using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Lifecycle;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [TestCase("preamble")]
        [TestCase("header")]
        [TestCase("invalid-header")]
        public void malformed_multipart_upload_should_report_bad_request_and_remove_temporary_files(string malformed)
        {
            var payload = malformed switch
            {
                "preamble" => new string('x', 20000) + "\r\n--backup--\r\n",
                "header" => "--backup\r\nContent-Disposition: " + new string('x', 20000) + "\r\n\r\n--backup--\r\n",
                _ => "--backup\r\ninvalid header\r\n\r\n--backup--\r\n"
            };
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            var folders = new AppFolders(Path.Combine(_root, "malformed-upload"));
            var controller = UploadContractController(folders, body);

            Assert.ThrowsAsync<BadRequestException>(async () => await controller.UploadAndRestore());
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [TestCase("Multipart body length limit exceeded.")]
        [TestCase("Limite de longueur depassee.")]
        [TestCase("Die Grenze wurde ueberschritten.")]
        public void invalid_upload_data_should_not_infer_size_from_exception_language(string message)
        {
            var folders = new AppFolders(Path.Combine(_root, "invalid-upload"));
            using var body = new UploadReadFailureStream(new InvalidDataException(message));
            var controller = UploadContractController(folders, body);

            var error = Assert.ThrowsAsync<BadRequestException>(async () => await controller.UploadAndRestore());
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [TestCase(false, "Request body too large.")]
        [TestCase(false, "Requete trop volumineuse.")]
        [TestCase(true, "Requete trop volumineuse.")]
        public void typed_upload_size_error_should_ignore_exception_language_and_claimed_content_length(bool dishonestLength, string message)
        {
            var folders = new AppFolders(Path.Combine(_root, "typed-upload-size"));
            using var body = new UploadReadFailureStream(new BadHttpRequestException(message, StatusCodes.Status413PayloadTooLarge));
            var controller = UploadContractController(folders, body);
            if (dishonestLength)
            {
                controller.Request.ContentLength = 1;
            }

            var error = Assert.ThrowsAsync<RestoreBackupFailedException>(async () => await controller.UploadAndRestore());
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [Test]
        public void cancelled_upload_should_remove_temporary_files_without_staging_or_restart()
        {
            var folders = new AppFolders(Path.Combine(_root, "cancelled-upload"));
            using var body = new UploadReadFailureStream(new OperationCanceledException());
            var controller = UploadContractController(folders, body);

            Assert.ThrowsAsync<OperationCanceledException>(async () => await controller.UploadAndRestore());
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [Test]
        public async Task valid_upload_should_stage_the_complete_file_despite_a_smaller_claimed_content_length()
        {
            const string payload = "synthetic backup payload";
            var folders = new AppFolders(Path.Combine(_root, "valid-upload"));
            using var multipart = new System.Net.Http.MultipartFormDataContent("backup");
            multipart.Add(new System.Net.Http.StringContent(payload), "restore", "backup.zip");
            using var body = await multipart.ReadAsStreamAsync();
            var staged = false;
            var restarted = false;
            var backup = InterfaceProxy.Proxy<IBackupService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBackupService.Restore)));
                Assert.That(File.ReadAllText((string)args[0]), Is.EqualTo(payload));
                staged = true;
                return null;
            });
            var lifecycle = InterfaceProxy.Proxy<ILifecycleService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ILifecycleService.Restart)));
                Assert.That(staged, Is.True);
                Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
                restarted = true;
                return null;
            });
            var controller = UploadContractController(folders, body, backup, lifecycle);
            controller.Request.ContentLength = 1;

            await controller.UploadAndRestore();
            Assert.That(staged && restarted, Is.True);
        }

        [TestCase(false, 256, HttpStatusCode.BadRequest)]
        [TestCase(true, 128, HttpStatusCode.BadRequest)]
        [TestCase(false, 257, HttpStatusCode.RequestEntityTooLarge)]
        [TestCase(true, 257, HttpStatusCode.RequestEntityTooLarge)]
        public async Task hosted_upload_should_apply_the_route_limit_to_the_complete_multipart_body(bool chunked, int bytes, HttpStatusCode expectedStatus)
        {
            var folders = new AppFolders(Path.Combine(_root, "hosted-upload"));
            var limit = new UploadTransportLimitFilter(256);
            _disk.AvailableSpace = long.MaxValue;
            await using var app = await StartUploadContractHost(folders, limit);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
            const string prefix = "--backup\r\nContent-Disposition: form-data; name=discarded\r\n\r\n";
            const string suffix = "\r\n--backup--\r\n";
            var payload = Encoding.UTF8.GetBytes(prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix);
            using HttpContent content = chunked ? new ChunkedUploadContent(payload) : new ByteArrayContent(payload);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=backup");
            using var response = await client.PostAsync("/api/v1/system/backup/restore/upload", content);

            Assert.Multiple(() =>
            {
                Assert.That(limit.RouteLimit, Is.EqualTo(BackupService.RestoreUploadLimitBytes));
                Assert.That(response.StatusCode, Is.EqualTo(expectedStatus));
                Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
            });
        }

        [Test]
        public async Task hosted_upload_should_reject_actual_oversized_content_length_before_reading_or_creating_a_work_folder()
        {
            var folders = new AppFolders(Path.Combine(_root, "hosted-content-length"));
            var limit = new UploadTransportLimitFilter(null);
            await using var app = await StartUploadContractHost(folders, limit);
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single());
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, address.Port);
            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"POST /api/v1/system/backup/restore/upload HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: multipart/form-data; boundary=backup\r\nContent-Length: {BackupService.RestoreUploadLimitBytes + 1}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request);
            using var response = new StreamReader(stream, Encoding.ASCII);
            var status = await response.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(limit.RouteLimit, Is.EqualTo(BackupService.RestoreUploadLimitBytes));
                Assert.That(status, Does.Contain("413"));
                Assert.That(Directory.Exists(folders.GetBackupWorkingFolder()), Is.False);
            });
        }

        private async Task<WebApplication> StartUploadContractHost(IAppFolderInfo folders, UploadTransportLimitFilter limit)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Limits.MaxRequestBodySize = 128;
            });
            builder.Services.AddCors(options => options.AddPolicy("ApiCorsPolicy", policy => policy.AllowAnyOrigin()));
            builder.Services.AddControllers(options => options.Filters.Add(limit))
                .AddApplicationPart(typeof(Chaptarr.Api.V1.System.Backup.BackupController).Assembly)
                .AddControllersAsServices();
            builder.Services.AddTransient(_ => new Chaptarr.Api.V1.System.Backup.BackupController(
                InterfaceProxy.Proxy<IBackupService>((_, _) => throw new AssertionException("Rejected upload must not stage")), folders, _disk,
                InterfaceProxy.Proxy<ILifecycleService>((_, _) => throw new AssertionException("Rejected upload must not restart"))));
            var app = builder.Build();
            app.UseExceptionHandler(handler => handler.Run(context => new Chaptarr.Http.ErrorManagement.ChaptarrErrorPipeline(_logger).HandleException(context)));
            app.UseCors();
            app.MapControllers();
            await app.StartAsync();
            return app;
        }

        private sealed class UploadTransportLimitFilter : IResourceFilter
        {
            private readonly long? _scaledLimit;

            public UploadTransportLimitFilter(long? scaledLimit)
            {
                _scaledLimit = scaledLimit;
            }

            public long? RouteLimit { get; private set; }

            public void OnResourceExecuting(ResourceExecutingContext context)
            {
                var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                RouteLimit = feature.MaxRequestBodySize;
                if (_scaledLimit.HasValue)
                {
                    feature.MaxRequestBodySize = _scaledLimit;
                }
            }

            public void OnResourceExecuted(ResourceExecutedContext context)
            {
            }
        }

        private sealed class ChunkedUploadContent : HttpContent
        {
            private readonly byte[] _payload;

            public ChunkedUploadContent(byte[] payload)
            {
                _payload = payload;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
                => stream.WriteAsync(_payload, 0, _payload.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        [TestCase("Multipart body length limit exceeded.")]
        [TestCase("Archive invalide.")]
        public async Task invalid_archive_upload_should_preserve_the_concrete_error_and_clean_up_without_restart(string message)
        {
            var folders = new AppFolders(Path.Combine(_root, "invalid-archive-upload"));
            using var multipart = new MultipartFormDataContent("backup");
            multipart.Add(new StringContent("synthetic invalid archive"), "restore", "backup.zip");
            using var body = await multipart.ReadAsStreamAsync();
            var backup = InterfaceProxy.Proxy<IBackupService>((_, _) => throw new InvalidDataException(message));
            var controller = UploadContractController(folders, body, backup);
            var error = Assert.ThrowsAsync<BadRequestException>(async () => await controller.UploadAndRestore());
            var context = controller.HttpContext;
            context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>(
                new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = error });
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;
            await new Chaptarr.Http.ErrorManagement.ChaptarrErrorPipeline(_logger).HandleException(context);

            Assert.Multiple(() =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(Encoding.UTF8.GetString(responseBody.ToArray()), Does.Contain(message));
                Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
            });
        }

        private Chaptarr.Api.V1.System.Backup.BackupController UploadContractController(IAppFolderInfo folders, Stream body,
            IBackupService backup = null, ILifecycleService lifecycle = null)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "multipart/form-data; boundary=backup";
            context.Request.Body = body;
            return new Chaptarr.Api.V1.System.Backup.BackupController(
                backup ?? InterfaceProxy.Proxy<IBackupService>((_, _) => throw new AssertionException("Rejected upload must not stage")),
                folders, _disk,
                lifecycle ?? InterfaceProxy.Proxy<ILifecycleService>((_, _) => throw new AssertionException("Rejected upload must not restart")))
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
        }

        private sealed class UploadReadFailureStream : MemoryStream
        {
            private readonly Exception _error;

            public UploadReadFailureStream(Exception error)
            {
                _error = error;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => ValueTask.FromException<int>(_error);
        }
    }
}
