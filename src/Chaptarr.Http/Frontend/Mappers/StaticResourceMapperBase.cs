using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace Chaptarr.Http.Frontend.Mappers
{
    public abstract class StaticResourceMapperBase : IMapHttpRequestsToDisk
    {
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly StringComparison _caseSensitive;
        private readonly IContentTypeProvider _mimeTypeProvider;

        protected StaticResourceMapperBase(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;

            _mimeTypeProvider = new FileExtensionContentTypeProvider();
            _caseSensitive = RuntimeInfo.IsProduction ? DiskProviderBase.PathStringComparison : StringComparison.OrdinalIgnoreCase;
        }

        public abstract string Map(string resourceUrl);

        public abstract bool CanHandle(string resourceUrl);

        protected abstract string GetAllowedRoot(string resourceUrl);

        public IActionResult GetResponse(string resourceUrl)
        {
            var mappedPath = Map(resourceUrl);
            var filePath = GetSafeFilePath(resourceUrl, mappedPath);

            if (filePath == null)
            {
                return null;
            }

            if (_diskProvider.FileExists(filePath, _caseSensitive))
            {
                if (!_mimeTypeProvider.TryGetContentType(filePath, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                Stream contentStream;
                try
                {
                    contentStream = GetContentStream(filePath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                {
                    _logger.Warn(ex, "Failed to open file {0}", filePath);
                    return null;
                }

                return new FileStreamResult(contentStream, new MediaTypeHeaderValue(contentType)
                {
                    Encoding = contentType == "text/plain" ? Encoding.UTF8 : null
                });
            }

            _logger.Warn("File {0} not found", filePath);

            return null;
        }

        private string GetSafeFilePath(string resourceUrl, string mappedPath)
        {
            if (mappedPath.IsNullOrWhiteSpace())
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(mappedPath);
            }
            catch
            {
                _logger.Warn("Refusing to serve invalid path for url: {0}", resourceUrl);
                return null;
            }

            var allowedRoot = GetAllowedRoot(resourceUrl);

            if (allowedRoot.IsNullOrWhiteSpace())
            {
                _logger.Warn("Refusing to serve file due to invalid root mapping for url: {0}", resourceUrl);
                return null;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(allowedRoot);
            }
            catch
            {
                _logger.Warn("Refusing to serve file due to invalid root mapping for url: {0}", resourceUrl);
                return null;
            }

            if (fullRoot.IsNullOrWhiteSpace())
            {
                _logger.Warn("Refusing to serve file due to empty root mapping for url: {0}", resourceUrl);
                return null;
            }

            if (string.Equals(fullRoot, Path.GetPathRoot(fullRoot), _caseSensitive))
            {
                _logger.Warn("Refusing to serve file due to missing/overly broad root mapping for url: {0}", resourceUrl);
                return null;
            }

            if (string.Equals(fullPath, fullRoot, _caseSensitive) || fullRoot.IsParentPath(fullPath))
            {
                if (!IsSymlinkAwareChildPath(fullRoot, fullPath))
                {
                    _logger.Warn("Refusing to serve file outside allowed root via symlink/junction. url={0} mapped={1}", resourceUrl, fullPath);
                    return null;
                }

                return fullPath;
            }

            _logger.Warn("Refusing to serve file outside allowed root. url={0} mapped={1}", resourceUrl, fullPath);
            return null;
        }

        private bool IsSymlinkAwareChildPath(string fullRoot, string fullPath)
        {
            try
            {
                var resolvedRoot = GetSymlinkAwareFullPath(fullRoot);
                var resolvedPath = GetSymlinkAwareFullPath(fullPath);

                if (resolvedRoot.IsNullOrWhiteSpace() || resolvedPath.IsNullOrWhiteSpace())
                {
                    // Best-effort only; fall back to the lexical containment checks already performed.
                    return true;
                }

                if (string.Equals(resolvedPath, resolvedRoot, _caseSensitive))
                {
                    return true;
                }

                return resolvedRoot.IsParentPath(resolvedPath);
            }
            catch
            {
                // Best-effort only; do not break common self-hosted mount patterns due to resolution failures.
                return true;
            }
        }

        private static string GetSymlinkAwareFullPath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            path = Path.GetFullPath(path);
            var root = Path.GetPathRoot(path);

            if (root.IsNullOrWhiteSpace())
            {
                return null;
            }

            var remainder = path.Substring(root.Length);
            var segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var next = Path.Combine(current, segments[i]);

                if (!Directory.Exists(next) && !File.Exists(next))
                {
                    for (var j = i; j < segments.Length; j++)
                    {
                        current = Path.Combine(current, segments[j]);
                    }

                    return current;
                }

                var isDir = Directory.Exists(next);
                FileSystemInfo info = isDir ? new DirectoryInfo(next) : new FileInfo(next);

                try
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                    current = resolved?.FullName ?? info.FullName;
                }
                catch
                {
                    current = info.FullName;
                }
            }

            return current;
        }

        protected virtual Stream GetContentStream(string filePath)
        {
            return File.OpenRead(filePath);
        }
    }
}
