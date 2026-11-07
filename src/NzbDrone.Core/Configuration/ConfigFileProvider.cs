using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Update;

namespace NzbDrone.Core.Configuration
{
    public interface IConfigFileProvider : IHandleAsync<ApplicationStartedEvent>,
        IExecute<ResetApiKeyCommand>
    {
        Dictionary<string, object> GetConfigDictionary();
        void SaveConfigDictionary(Dictionary<string, object> configValues);
        void EnsureDefaultConfigFile();

        string BindAddress { get; }
        int Port { get; }
        int SslPort { get; }
        bool EnableSsl { get; }
        bool LaunchBrowser { get; }
        AuthenticationType ConfiguredAuthenticationMethod { get; }
        AuthenticationType AuthenticationMethod { get; }
        bool AuthenticationMethodIsMisconfigured { get; }
        AuthenticationRequiredType AuthenticationRequired { get; }
        string AuthCookieStamp { get; }
        string LogLevel { get; }
        string ConsoleLogLevel { get; }
        bool LogSql { get; }
        int LogRotate { get; }
        bool FilterSentryEvents { get; }
        string Branch { get; }
        string ApiKey { get; }
        string SslCertPath { get; }
        string SslCertPassword { get; }
        string UrlBase { get; }
        string UiFolder { get; }
        string InstanceName { get; }
        string GetBookshelfInstanceName() => InstanceName;
        string InstallationId { get; }
        bool UpdateAutomatically { get; }
        UpdateMechanism UpdateMechanism { get; }
        string UpdateScriptPath { get; }
        string SyslogServer { get; }
        int SyslogPort { get; }
        string SyslogLevel { get; }
        string SeerrApiKey { get; }
        string Theme { get; }
        string PostgresHost { get; }
        int PostgresPort { get; }
        string PostgresUser { get; }
        string PostgresPassword { get; }
        string PostgresMainDb { get; }
        string PostgresLogDb { get; }
        string PostgresCacheDb { get; }
        bool IsPostgresMainDbExplicitlyConfigured();
        bool IsPostgresLogDbExplicitlyConfigured();
        bool IsPostgresCacheDbExplicitlyConfigured();
        string PostgresSslMode { get; }
        string PostgresRootCert { get; }
        bool PostgresNoResetOnClose { get; }
        bool TrustCgnatIpAddresses { get; }
        string OidcAuthority { get; }
        string OidcClientId { get; }
        string OidcClientSecret { get; }
        string OidcScopes { get; }
        string OidcAllowedEmails { get; }
        string OidcAllowedEmailDomains { get; }
        bool OidcRequireEmailVerified { get; }
        bool OidcAllowAnyVerifiedUser { get; }

        string PlexAuthUserId { get; }
        string PlexAuthUsername { get; }
    }

    public class ConfigFileProvider : IConfigFileProvider
    {
        public const string CONFIG_ELEMENT_NAME = "Config";

        private readonly IEventAggregator _eventAggregator;
        private readonly IDiskProvider _diskProvider;
        private readonly ICached<string> _cache;
        private readonly PostgresOptions _postgresOptions;
        private readonly AuthOptions _authOptions;
        private readonly AppOptions _appOptions;
        private readonly ServerOptions _serverOptions;
        private readonly UpdateOptions _updateOptions;
        private readonly LogOptions _logOptions;

        private readonly string _configFile;

        private static readonly object Mutex = new object();

        public ConfigFileProvider(IAppFolderInfo appFolderInfo,
                                  ICacheManager cacheManager,
                                  IEventAggregator eventAggregator,
                                  IDiskProvider diskProvider,
                                  IOptions<PostgresOptions> postgresOptions,
                                  IOptions<AuthOptions> authOptions,
                                  IOptions<AppOptions> appOptions,
                                  IOptions<ServerOptions> serverOptions,
                                  IOptions<UpdateOptions> updateOptions,
                                  IOptions<LogOptions> logOptions)
        {
            _cache = cacheManager.GetCache<string>(GetType());
            _eventAggregator = eventAggregator;
            _diskProvider = diskProvider;
            _configFile = appFolderInfo.GetConfigPath();
            _postgresOptions = postgresOptions.Value;
            _authOptions = authOptions.Value;
            _appOptions = appOptions.Value;
            _serverOptions = serverOptions.Value;
            _updateOptions = updateOptions.Value;
            _logOptions = logOptions.Value;
        }

        public Dictionary<string, object> GetConfigDictionary()
        {
            var dict = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);

            var type = GetType();
            var properties = type.GetProperties();

            foreach (var propertyInfo in properties)
            {
                var value = propertyInfo.GetValue(this, null);

                dict.Add(propertyInfo.Name, value);
            }

            return dict;
        }

        public void SaveConfigDictionary(Dictionary<string, object> configValues)
        {
            _cache.Clear();

            var allWithDefaults = GetConfigDictionary();

            foreach (var configValue in configValues)
            {
                if (configValue.Key.Equals("ApiKey", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                allWithDefaults.TryGetValue(configValue.Key, out var currentValue);
                if (currentValue == null)
                {
                    continue;
                }

                var equal = configValue.Value.ToString().Equals(currentValue.ToString());

                if (!equal)
                {
                    SetValue(configValue.Key.FirstCharToUpper(), configValue.Value.ToString());
                }
            }

            _eventAggregator.PublishEvent(new ConfigFileSavedEvent());
        }

        public string BindAddress
        {
            get
            {
                const string defaultValue = "*";

                var bindAddress = _serverOptions.BindAddress ?? GetValue("BindAddress", defaultValue);
                if (string.IsNullOrWhiteSpace(bindAddress))
                {
                    return defaultValue;
                }

                return bindAddress;
            }
        }

        public int Port
        {
            get
            {
                if (_serverOptions.Port.HasValue)
                {
                    return _serverOptions.Port.Value;
                }

                var configuredPort = GetValue("Port", string.Empty, persist: false);
                if (!string.IsNullOrWhiteSpace(configuredPort))
                {
                    return Convert.ToInt32(configuredPort);
                }

                var defaultPort = ServerConfigurationDefaults.Port;
                SetValue("Port", defaultPort);
                return defaultPort;
            }
        }

        public int SslPort => _serverOptions.SslPort ?? GetValueInt("SslPort", 6868);

        public bool EnableSsl => _serverOptions.EnableSsl ?? GetValueBoolean("EnableSsl", false);

        public bool LaunchBrowser => _appOptions.LaunchBrowser ?? GetValueBoolean("LaunchBrowser", true);

        public string ApiKey
        {
            get
            {
                var apiKey = _authOptions.ApiKey ?? GetValue("ApiKey", GenerateApiKey());

                if (apiKey.IsNullOrWhiteSpace())
                {
                    apiKey = GenerateApiKey();
                    SetValue("ApiKey", apiKey);
                }

                return apiKey;
            }
        }

        public string SeerrApiKey => _authOptions.SeerrApiKey?.Trim();

        public AuthenticationType ConfiguredAuthenticationMethod
        {
            get
            {
                var enabled = _authOptions.Enabled ?? GetValueBoolean("AuthenticationEnabled", false, false);

                if (enabled)
                {
                    SetValue("AuthenticationMethod", AuthenticationType.Basic);
                    return AuthenticationType.Basic;
                }

                return Enum.TryParse<AuthenticationType>(_authOptions.Method, out var enumValue)
                    ? enumValue
                    : GetValueEnum("AuthenticationMethod", AuthenticationType.None);
            }
        }

        public bool AuthenticationMethodIsMisconfigured =>
            ConfiguredAuthenticationMethod == AuthenticationType.Oidc &&
            (OidcAuthority.IsNullOrWhiteSpace() || OidcClientId.IsNullOrWhiteSpace() || OidcClientSecret.IsNullOrWhiteSpace());

        public AuthenticationType AuthenticationMethod
        {
            get
            {
                var method = ConfiguredAuthenticationMethod;

                // Local recovery uses the no-auth handler while the UI authorization boundary
                // keeps remote requests closed until OIDC is complete.
                if (AuthenticationMethodIsMisconfigured)
                {
                    return AuthenticationType.None;
                }

                return method;
            }
        }

        public AuthenticationRequiredType AuthenticationRequired =>
            Enum.TryParse<AuthenticationRequiredType>(_authOptions.Required, out var enumValue)
                ? enumValue
                : GetValueEnum("AuthenticationRequired", AuthenticationRequiredType.Enabled);

        public string AuthCookieStamp
        {
            get
            {
                var stamp = GetValue("AuthCookieStamp", Guid.NewGuid().ToString("N"));

                if (stamp.IsNullOrWhiteSpace())
                {
                    stamp = Guid.NewGuid().ToString("N");
                    SetValue("AuthCookieStamp", stamp);
                }

                return stamp;
            }
        }

        public string OidcAuthority => GetBookshelfOidcSetting("OidcAuthority", BookshelfOidcEnvironmentSettings.Authority).Trim();
        public string OidcClientId => GetBookshelfOidcSetting("OidcClientId", BookshelfOidcEnvironmentSettings.ClientId).Trim();
        public string OidcClientSecret => GetBookshelfOidcSetting("OidcClientSecret", BookshelfOidcEnvironmentSettings.ClientSecret);
        public string OidcScopes => GetBookshelfOidcSetting("OidcScopes", BookshelfOidcEnvironmentSettings.Scopes, "openid profile email").Trim();
        public string OidcAllowedEmails => GetBookshelfOidcSetting("OidcAllowedEmails", BookshelfOidcEnvironmentSettings.AllowedEmails).Trim();
        public string OidcAllowedEmailDomains => GetBookshelfOidcSetting("OidcAllowedEmailDomains", BookshelfOidcEnvironmentSettings.AllowedEmailDomains).Trim();
        // Retained as a read-only compatibility setting. OIDC admission always
        // requires an affirmative email_verified claim.
        public bool OidcRequireEmailVerified => true;
        public bool OidcAllowAnyVerifiedUser => BookshelfOidcEnvironmentSettings.ResolveBoolean(
            GetValue("OidcAllowAnyVerifiedUser", string.Empty, persist: false),
            BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser,
            false);

        public string PlexAuthUserId => GetValue("PlexAuthUserId", string.Empty).Trim();
        public string PlexAuthUsername => GetValue("PlexAuthUsername", string.Empty).Trim();

        private string GetBookshelfOidcSetting(string key, string environmentVariable, string defaultValue = "")
        {
            return BookshelfOidcEnvironmentSettings.Resolve(
                GetValue(key, string.Empty, persist: false),
                environmentVariable,
                defaultValue);
        }

        public string Branch => NormalizeReleaseBranch(_updateOptions.Branch ?? GetValue("Branch", "main"));

        public string LogLevel => _logOptions.Level ?? GetValue("LogLevel", "info").ToLowerInvariant();
        public string ConsoleLogLevel => _logOptions.ConsoleLevel ?? GetValue("ConsoleLogLevel", string.Empty, persist: false);

        public string PostgresHost => _postgresOptions?.Host ?? GetValue("PostgresHost", string.Empty, persist: false);
        public string PostgresUser => _postgresOptions?.User ?? GetValue("PostgresUser", string.Empty, persist: false);
        public string PostgresPassword => _postgresOptions?.Password ?? GetValue("PostgresPassword", string.Empty, persist: false);
        public string PostgresMainDb => _postgresOptions?.MainDb ?? GetValue("PostgresMainDb", "chaptarr-main", persist: false);
        public string PostgresLogDb => _postgresOptions?.LogDb ?? GetValue("PostgresLogDb", "chaptarr-log", persist: false);
        public string PostgresCacheDb => _postgresOptions?.CacheDb ?? GetValue("PostgresCacheDb", "chaptarr-cache", persist: false);
        public bool IsPostgresMainDbExplicitlyConfigured() => IsPostgresDatabaseNameExplicitlyConfigured("PostgresMainDb", _postgresOptions?.MainDb);
        public bool IsPostgresLogDbExplicitlyConfigured() => IsPostgresDatabaseNameExplicitlyConfigured("PostgresLogDb", _postgresOptions?.LogDb);
        public bool IsPostgresCacheDbExplicitlyConfigured() => IsPostgresDatabaseNameExplicitlyConfigured("PostgresCacheDb", _postgresOptions?.CacheDb);
        public string PostgresSslMode => _postgresOptions?.SslMode ?? GetValue("PostgresSslMode", string.Empty, persist: false);
        public string PostgresRootCert => _postgresOptions?.RootCert ?? GetValue("PostgresRootCert", string.Empty, persist: false);
        public bool PostgresNoResetOnClose => _postgresOptions?.NoResetOnClose ?? GetValueBoolean("PostgresNoResetOnClose", true, persist: false);
        public int PostgresPort => (_postgresOptions?.Port ?? 0) != 0 ? _postgresOptions.Port : GetValueInt("PostgresPort", 5432, persist: false);

        public string Theme => _appOptions.Theme ?? GetValue("Theme", "auto", persist: false);
        public bool LogSql => _logOptions.Sql ?? GetValueBoolean("LogSql", false, persist: false);
        public int LogRotate => _logOptions.Rotate ?? GetValueInt("LogRotate", 50, persist: false);
        public bool FilterSentryEvents => _logOptions.FilterSentryEvents ?? GetValueBoolean("FilterSentryEvents", true, persist: false);
        public string SslCertPath => _serverOptions.SslCertPath ?? GetValue("SslCertPath", "");
        public string SslCertPassword => _serverOptions.SslCertPassword ?? GetValue("SslCertPassword", "");

        public string UrlBase
        {
            get
            {
                var urlBase = (_serverOptions.UrlBase ?? GetValue("UrlBase", "")).Trim('/');

                if (urlBase.IsNullOrWhiteSpace())
                {
                    return urlBase;
                }

                return "/" + urlBase;
            }
        }

        public string UiFolder => BuildInfo.IsDebug ? Path.Combine("..", "UI") : "UI";

        public string InstanceName
        {
            get
            {
                var instanceName = _appOptions.InstanceName ?? GetValue("InstanceName", BuildInfo.AppName);

                if (instanceName.Contains(BuildInfo.AppName, StringComparison.OrdinalIgnoreCase))
                {
                    return instanceName;
                }

                return BuildInfo.AppName;
            }
        }

        public string GetBookshelfInstanceName()
        {
            var name = _appOptions.InstanceName ?? GetValue("InstanceName", "Readarr", false);
            return ResolveBookshelfInstanceName(name);
        }

        internal static string ResolveBookshelfInstanceName(string name)
        {
            // Match the predecessor's default and accepted custom instance names.
            return name?.Contains("Readarr", StringComparison.OrdinalIgnoreCase) == true ? name : "Readarr";
        }

        public string InstallationId => GetValue("InstallationId", Guid.NewGuid().ToString("N"), true);

        public bool UpdateAutomatically => _updateOptions.Automatically ?? GetValueBoolean("UpdateAutomatically", OsInfo.IsWindows, false);

        public UpdateMechanism UpdateMechanism =>
            Enum.TryParse<UpdateMechanism>(_updateOptions.Mechanism, out var enumValue)
                ? enumValue
                : GetValueEnum("UpdateMechanism", UpdateMechanism.BuiltIn, false);

        public string UpdateScriptPath => _updateOptions.ScriptPath ?? GetValue("UpdateScriptPath", "", false);

        public string SyslogServer => _logOptions.SyslogServer ?? GetValue("SyslogServer", "", persist: false);

        public int SyslogPort => _logOptions.SyslogPort ?? GetValueInt("SyslogPort", 514, persist: false);

        public string SyslogLevel => _logOptions.SyslogLevel ?? GetValue("SyslogLevel", LogLevel, persist: false).ToLowerInvariant();

        public int GetValueInt(string key, int defaultValue, bool persist = true)
        {
            return Convert.ToInt32(GetValue(key, defaultValue, persist));
        }

        public bool GetValueBoolean(string key, bool defaultValue, bool persist = true)
        {
            return Convert.ToBoolean(GetValue(key, defaultValue, persist));
        }

        public T GetValueEnum<T>(string key, T defaultValue, bool persist = true)
        {
            return (T)Enum.Parse(typeof(T), GetValue(key, defaultValue, persist), true);
        }

        public string GetValue(string key, object defaultValue, bool persist = true)
        {
            return _cache.Get(key, () =>
                {
                    var xDoc = LoadConfigFile();
                    var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

                    var parentContainer = config;

                    var valueHolder = parentContainer.Descendants(key).ToList();

                    if (valueHolder.Count() == 1)
                    {
                        return valueHolder.First().Value.Trim();
                    }

                    //Save the value
                    if (persist)
                    {
                        SetValue(key, defaultValue);
                    }

                    //return the default value
                    return defaultValue.ToString();
                });
        }

        private bool IsPostgresDatabaseNameExplicitlyConfigured(string key, string environmentValue)
        {
            if (environmentValue != null)
            {
                return true;
            }

            var config = LoadConfigFile().Descendants(CONFIG_ELEMENT_NAME).Single();
            return config.Descendants(key).Any();
        }

        public void SetValue(string key, object value)
        {
            var valueString = value.ToString().Trim();
            var xDoc = LoadConfigFile();
            var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

            var parentContainer = config;

            var keyHolder = parentContainer.Descendants(key);

            if (keyHolder.Count() != 1)
            {
                parentContainer.Add(new XElement(key, valueString));
            }
            else
            {
                parentContainer.Descendants(key).Single().Value = valueString;
            }

            _cache.Set(key, valueString);

            SaveConfigFile(xDoc);
        }

        public void SetValue(string key, Enum value)
        {
            SetValue(key, value.ToString().ToLower());
        }

        public void EnsureDefaultConfigFile()
        {
            if (!File.Exists(_configFile))
            {
                SaveConfigDictionary(GetConfigDictionary());
            }
        }

        public void MigrateConfigFile()
        {
            if (!File.Exists(_configFile))
            {
                return;
            }

            // If SSL is enabled and a cert hash is still in the config file or cert path is empty disable SSL
            if (EnableSsl && (GetValue("SslCertHash", string.Empty, false).IsNotNullOrWhiteSpace() || SslCertPath.IsNullOrWhiteSpace()))
            {
                SetValue("EnableSsl", false);
            }
        }

        private void DeleteOldValues()
        {
            var xDoc = LoadConfigFile();
            var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

            var type = GetType();
            var properties = type.GetProperties();

            foreach (var configValue in config.Descendants().ToList())
            {
                var name = configValue.Name.LocalName;

                if (!properties.Any(p => p.Name == name))
                {
                    config.Descendants(name).Remove();
                }
            }

            SaveConfigFile(xDoc);
        }

        private XDocument LoadConfigFile()
        {
            try
            {
                lock (Mutex)
                {
                    if (_diskProvider.FileExists(_configFile))
                    {
                        var contents = _diskProvider.ReadAllText(_configFile);

                        if (contents.IsNullOrWhiteSpace())
                        {
                            throw new InvalidConfigFileException($"{_configFile} is empty. Please delete the config file and Chaptarr will recreate it.");
                        }

                        if (contents.All(char.IsControl))
                        {
                            throw new InvalidConfigFileException($"{_configFile} is corrupt. Please delete the config file and Chaptarr will recreate it.");
                        }

                        var xDoc = XDocument.Parse(_diskProvider.ReadAllText(_configFile));
                        var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).ToList();

                        if (config.Count != 1)
                        {
                            throw new InvalidConfigFileException($"{_configFile} is invalid. Please delete the config file and Chaptarr will recreate it.");
                        }

                        return xDoc;
                    }

                    var newXDoc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"));
                    newXDoc.Add(new XElement(CONFIG_ELEMENT_NAME));

                    return newXDoc;
                }
            }
            catch (XmlException ex)
            {
                throw new InvalidConfigFileException($"{_configFile} is corrupt is invalid. Please delete the config file and Chaptarr will recreate it.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new AccessDeniedConfigFileException($"Chaptarr does not have access to config file: {_configFile}. Please fix permissions", ex);
            }
        }

        private void SaveConfigFile(XDocument xDoc)
        {
            try
            {
                lock (Mutex)
                {
                    _diskProvider.WriteAllText(_configFile, xDoc.ToString());
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new AccessDeniedConfigFileException($"Chaptarr does not have access to config file: {_configFile}. Please fix permissions", ex);
            }
        }

        private string GenerateApiKey()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            MigrateConfigFile();
            EnsureDefaultConfigFile();
            DeleteOldValues();
        }

        public void Execute(ResetApiKeyCommand message)
        {
            SetValue("ApiKey", GenerateApiKey());
        }

        public bool TrustCgnatIpAddresses => _authOptions.TrustCgnatIpAddresses ?? GetValueBoolean("TrustCgnatIpAddresses", false, persist: false);

        private static string NormalizeReleaseBranch(string branch)
        {
            var normalized = (branch ?? "main").Trim().ToLowerInvariant();
            return normalized == "develop" || normalized == "master" || normalized == "nightly"
                ? "main"
                : normalized;
        }
    }
}
