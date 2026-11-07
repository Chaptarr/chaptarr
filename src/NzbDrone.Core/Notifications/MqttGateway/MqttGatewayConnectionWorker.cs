using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    internal sealed class MqttGatewayConnectionWorker
    {
        private const int EventQueueCapacity = 256;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly NotificationDefinition _definition;
        private readonly MqttGatewaySettings _settings;
        private readonly MqttGatewaySnapshotProvider _snapshotProvider;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Logger _logger;
        private readonly NotificationText _notificationText;
        private readonly string _configurationFingerprint;
        private readonly Channel<WorkItem> _queue;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _runTask;
        private string _lastSnapshotFingerprint;
        private DateTime _nextSnapshotAt;
        private int _snapshotQueued;
        private int _cleanupOnStop;

        public MqttGatewayConnectionWorker(NotificationDefinition definition,
                                           MqttGatewaySettings settings,
                                           MqttGatewaySnapshotProvider snapshotProvider,
                                           IConfigFileProvider configFileProvider,
                                           NotificationText notificationText,
                                           Logger logger)
        {
            _definition = definition;
            _settings = settings;
            _snapshotProvider = snapshotProvider;
            _configFileProvider = configFileProvider;
            _logger = logger;
            _notificationText = notificationText;
            _configurationFingerprint = notificationText.LocaleFingerprint(MqttGatewayMqtt.GetSettingsFingerprint(settings));
            _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(EventQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _nextSnapshotAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.SensorRefreshIntervalMinutes));
            _runTask = Task.Run(RunAsync);
        }

        public string ConfigurationFingerprint()
        {
            return _configurationFingerprint;
        }

        public bool TryPublishEvent(MqttGatewayEvent gatewayEvent)
        {
            if (!_settings.PublishEvents)
            {
                return true;
            }

            if (_queue.Writer.TryWrite(new WorkItem(WorkKind.Event, gatewayEvent)))
            {
                return true;
            }

            _logger.Warn("MQTT event queue is full for connection {0}; dropped event {1}", _definition.Name, gatewayEvent.Event);
            return false;
        }

        public void RequestSnapshot()
        {
            if (!_settings.PublishSensors || Interlocked.CompareExchange(ref _snapshotQueued, 1, 0) != 0)
            {
                return;
            }

            if (!_queue.Writer.TryWrite(new WorkItem(WorkKind.Snapshot)))
            {
                Interlocked.Exchange(ref _snapshotQueued, 0);
                _logger.Warn("MQTT event queue is full for connection {0}; a sensor refresh was deferred", _definition.Name);
            }
        }

        public async Task StopAsync(bool cleanDiscovery)
        {
            if (cleanDiscovery)
            {
                Interlocked.Exchange(ref _cleanupOnStop, 1);
            }

            _shutdown.Cancel();
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn("MQTT gateway connection {0} did not stop before the shutdown timeout", _definition.Name);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunAsync()
        {
            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();
            client.ApplicationMessageReceivedAsync += HandleIncomingMessageAsync;
            client.DisconnectedAsync += HandleDisconnectedAsync;

            var rootTopic = MqttGatewayMqtt.GetRootTopic(_settings);
            var availabilityTopic = $"{rootTopic}/status";
            var discoveryTopic = GetDiscoveryTopic();
            var reconnectDelaySeconds = 2;

            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    if (!client.IsConnected)
                    {
                        try
                        {
                            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                            var result = await client.ConnectAsync(MqttGatewayMqtt.BuildClientOptions(_settings, availabilityTopic), connectTimeout.Token).ConfigureAwait(false);
                            if (!client.IsConnected)
                            {
                                throw new InvalidOperationException($"Broker rejected MQTT connection: {result.ResultCode}");
                            }

                            reconnectDelaySeconds = 2;
                            _nextSnapshotAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.SensorRefreshIntervalMinutes));
                            await PublishAvailabilityAsync(client, availabilityTopic, "online", _shutdown.Token).ConfigureAwait(false);
                            await SubscribeToHomeAssistantBirthAsync(client, _shutdown.Token).ConfigureAwait(false);
                            await PublishDiscoveryAsync(client, discoveryTopic, _shutdown.Token).ConfigureAwait(false);
                            await PublishSnapshotAsync(client, true, _shutdown.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                        {
                            break;
                        }
                        catch
                        {
                            _logger.Warn("Unable to connect MQTT gateway {0}; retrying", _definition.Name);
                            if (client.IsConnected)
                            {
                                try
                                {
                                    await PublishAvailabilityAsync(client, availabilityTopic, "offline", _shutdown.Token).ConfigureAwait(false);
                                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), _shutdown.Token).ConfigureAwait(false);
                                }
                                catch
                                {
                                }
                            }

                            var jitter = Random.Shared.Next(0, Math.Max(2, reconnectDelaySeconds * 1000 / 2));
                            await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds) + TimeSpan.FromMilliseconds(jitter), _shutdown.Token).ConfigureAwait(false);
                            reconnectDelaySeconds = Math.Min(reconnectDelaySeconds * 2, 300);
                            continue;
                        }
                    }

                    WorkItem command = null;
                    try
                    {
                        command = await ReadNextWorkItemAsync(_shutdown.Token).ConfigureAwait(false);
                        if (command == null)
                        {
                            await PublishSnapshotAsync(client, false, _shutdown.Token).ConfigureAwait(false);
                            _nextSnapshotAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.SensorRefreshIntervalMinutes));
                            continue;
                        }

                        if (command.Kind == WorkKind.Reconnect)
                        {
                            // A disconnect callback can enqueue this while the main loop is waiting
                            // for work. Returning to the top lets the normal reconnect path run.
                            continue;
                        }

                        if (command.Kind == WorkKind.HomeAssistantBirth)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 5000)), _shutdown.Token).ConfigureAwait(false);
                            await PublishDiscoveryAsync(client, discoveryTopic, _shutdown.Token).ConfigureAwait(false);
                            await PublishSnapshotAsync(client, true, _shutdown.Token).ConfigureAwait(false);
                        }
                        else if (command.Kind == WorkKind.Snapshot)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(750), _shutdown.Token).ConfigureAwait(false);
                            Interlocked.Exchange(ref _snapshotQueued, 0);
                            await PublishSnapshotAsync(client, false, _shutdown.Token).ConfigureAwait(false);
                        }
                        else if (command.Kind == WorkKind.Event)
                        {
                            await PublishEventAsync(client, command.Event, _shutdown.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        _logger.Warn("MQTT gateway connection {0} lost broker connectivity; reconnecting", _definition.Name);
                        if (command?.Kind == WorkKind.Event && !_queue.Writer.TryWrite(command))
                        {
                            _logger.Warn("MQTT event queue is full for connection {0}; dropped event {1} after a publish failure", _definition.Name, command.Event.Event);
                        }

                        try
                        {
                            if (client.IsConnected)
                            {
                                await PublishAvailabilityAsync(client, availabilityTopic, "offline", _shutdown.Token).ConfigureAwait(false);
                            }

                            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), _shutdown.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                if (client.IsConnected)
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    if (Volatile.Read(ref _cleanupOnStop) != 0)
                    {
                        try
                        {
                            await ClearDiscoveryAsync(client, discoveryTopic, cleanupTimeout.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    try
                    {
                        await PublishAvailabilityAsync(client, availabilityTopic, "offline", cleanupTimeout.Token).ConfigureAwait(false);
                        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cleanupTimeout.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                _queue.Writer.TryComplete();
            }
        }

        private async Task<WorkItem> ReadNextWorkItemAsync(CancellationToken cancellationToken)
        {
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = _queue.Reader.ReadAsync(waitCancellation.Token).AsTask();
            var timeUntilSnapshot = _settings.PublishSensors
                ? _nextSnapshotAt - DateTime.UtcNow
                : Timeout.InfiniteTimeSpan;
            var timeoutTask = timeUntilSnapshot == Timeout.InfiniteTimeSpan
                ? Task.Delay(Timeout.Infinite, waitCancellation.Token)
                : Task.Delay(timeUntilSnapshot < TimeSpan.Zero ? TimeSpan.Zero : timeUntilSnapshot, waitCancellation.Token);
            var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
            waitCancellation.Cancel();

            if (completed == readTask)
            {
                try
                {
                    return await readTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            try
            {
                await timeoutTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return null;
        }

        private async Task HandleIncomingMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            if (!_settings.PublishHomeAssistantDiscovery || !_settings.PublishSensors ||
                !string.Equals(args.ApplicationMessage.Topic, $"{_settings.DiscoveryPrefix.Trim('/')}/status", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());
            if (string.Equals(payload.Trim(), "online", StringComparison.OrdinalIgnoreCase))
            {
                _queue.Writer.TryWrite(new WorkItem(WorkKind.HomeAssistantBirth));
            }
        }

        private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                _queue.Writer.TryWrite(new WorkItem(WorkKind.Reconnect));
            }

            return Task.CompletedTask;
        }

        private async Task SubscribeToHomeAssistantBirthAsync(IMqttClient client, CancellationToken cancellationToken)
        {
            if (!_settings.PublishHomeAssistantDiscovery || !_settings.PublishSensors)
            {
                return;
            }

            var options = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic($"{_settings.DiscoveryPrefix.Trim('/')}/status")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build();
            await client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
        }

        private async Task PublishSnapshotAsync(IMqttClient client, bool force, CancellationToken cancellationToken)
        {
            if (!_settings.PublishSensors)
            {
                return;
            }

            MqttGatewaySnapshot snapshot;
            try
            {
                snapshot = _snapshotProvider.Create(_settings);
            }
            catch
            {
                _logger.Warn("Unable to create MQTT sensor snapshot for connection {0}", _definition.Name);
                return;
            }

            var fingerprint = snapshot.StateFingerprint();
            if (!force && string.Equals(fingerprint, _lastSnapshotFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var message = MqttGatewayMqtt.BuildMessage($"{MqttGatewayMqtt.GetRootTopic(_settings)}/state", json, MqttGatewayQos.AtLeastOnce);
            await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            _lastSnapshotFingerprint = fingerprint;
        }

        private async Task PublishEventAsync(IMqttClient client, MqttGatewayEvent gatewayEvent, CancellationToken cancellationToken)
        {
            var topic = MqttGatewayMqtt.GetEventTopic(_settings, gatewayEvent.Event);
            await client.PublishAsync(MqttGatewayMqtt.BuildMessage(topic, gatewayEvent.ToJson(), _settings.EventQos), cancellationToken).ConfigureAwait(false);
        }

        private async Task PublishAvailabilityAsync(IMqttClient client, string topic, string payload, CancellationToken cancellationToken)
        {
            await client.PublishAsync(MqttGatewayMqtt.BuildAvailabilityMessage(topic, payload), cancellationToken).ConfigureAwait(false);
        }

        private async Task PublishDiscoveryAsync(IMqttClient client, string topic, CancellationToken cancellationToken)
        {
            if (!_settings.PublishHomeAssistantDiscovery)
            {
                await ClearDiscoveryAsync(client, topic, cancellationToken).ConfigureAwait(false);
                return;
            }

            var rootTopic = MqttGatewayMqtt.GetRootTopic(_settings);
            var stateTopic = $"{rootTopic}/state";
            var availabilityTopic = $"{rootTopic}/status";
            var instance = _settings.InstanceId;
            var components = new Dictionary<string, object>
            {
                ["books_total"] = CreateSensor(instance, "books_total", _notificationText.Get("notification.sensor.booksTotal", "Books Total"), "sensor.chaptarr_books_total", stateTopic, availabilityTopic, "{{ value_json.books_total }}", stateClass: "measurement"),
                ["audiobooks_total"] = CreateSensor(instance, "audiobooks_total", _notificationText.Get("notification.sensor.audiobooksTotal", "Audiobooks Total"), "sensor.chaptarr_audiobooks_total", stateTopic, availabilityTopic, "{{ value_json.audiobooks_total }}", stateClass: "measurement"),
                ["missing_books"] = CreateSensor(instance, "missing_books", _notificationText.Get("notification.sensor.missingBooks", "Missing Books"), "sensor.chaptarr_missing_books", stateTopic, availabilityTopic, "{{ value_json.missing_books }}", stateClass: "measurement"),
                ["wanted_books"] = CreateSensor(instance, "wanted_books", _notificationText.Get("notification.sensor.wantedBooks", "Wanted Books"), "sensor.chaptarr_wanted_books", stateTopic, availabilityTopic, "{{ value_json.wanted_books }}", stateClass: "measurement"),
                ["queue"] = CreateSensor(instance, "queue", _notificationText.Get("notification.sensor.queue", "Queue"), "sensor.chaptarr_queue", stateTopic, availabilityTopic, "{{ value_json.queue }}", stateClass: "measurement"),
                ["upcoming_releases"] = CreateSensor(instance, "upcoming_releases", _notificationText.Get("notification.sensor.upcomingReleases", "Upcoming Releases"), "sensor.chaptarr_upcoming_releases", stateTopic, availabilityTopic, "{{ value_json.upcoming_releases }}", stateClass: "measurement"),
                ["library_size"] = CreateSensor(instance, "library_size", _notificationText.Get("notification.sensor.librarySize", "Library Size"), "sensor.chaptarr_library_size", stateTopic, availabilityTopic, "{{ value_json.library_size }}", deviceClass: "data_size", unit: "B", stateClass: "measurement"),
                ["last_import"] = CreateSensor(instance, "last_import", _notificationText.Get("notification.sensor.lastImport", "Last Import"), "sensor.chaptarr_last_import", stateTopic, availabilityTopic, "{{ value_json.last_import or none }}", deviceClass: "timestamp"),
                ["health"] = CreateSensor(instance, "health", _notificationText.Get("notification.sensor.health", "Health"), "sensor.chaptarr_health", stateTopic, availabilityTopic, "{{ value_json.health }}", deviceClass: "enum", options: new[] { "ok", "warning", "error" }, icon: "mdi:heart-pulse")
            };

            var payload = new Dictionary<string, object>
            {
                ["device"] = new Dictionary<string, object>
                {
                    ["identifiers"] = new[] { $"chaptarr_{instance}" },
                    ["name"] = _definition.Name,
                    ["manufacturer"] = BuildInfo.AppName,
                    ["model"] = "Audiobook and ebook library",
                    ["sw_version"] = BuildInfo.Version.ToString(),
                    ["configuration_url"] = GetConfigurationUrl()
                },
                ["origin"] = new Dictionary<string, object>
                {
                    ["name"] = BuildInfo.AppName,
                    ["sw_version"] = BuildInfo.Version.ToString(),
                    ["support_url"] = "https://github.com/Chaptarr/chaptarr"
                },
                ["components"] = components,
                ["qos"] = (int)MqttGatewayQos.AtLeastOnce
            };

            var message = MqttGatewayMqtt.BuildMessage(topic, JsonSerializer.Serialize(payload, JsonOptions), MqttGatewayQos.AtLeastOnce, true);
            await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private static Dictionary<string, object> CreateSensor(string instanceId,
                                                                string key,
                                                                string name,
                                                                string defaultEntityId,
                                                                string stateTopic,
                                                                string availabilityTopic,
                                                                string valueTemplate,
                                                                string deviceClass = null,
                                                                string unit = null,
                                                                string stateClass = null,
                                                                string[] options = null,
                                                                string icon = null)
        {
            var sensor = new Dictionary<string, object>
            {
                ["platform"] = "sensor",
                ["name"] = name,
                ["unique_id"] = $"chaptarr_{instanceId}_{key}",
                ["default_entity_id"] = defaultEntityId,
                ["state_topic"] = stateTopic,
                ["availability_topic"] = availabilityTopic,
                ["payload_available"] = "online",
                ["payload_not_available"] = "offline",
                ["value_template"] = valueTemplate
            };

            if (deviceClass != null)
            {
                sensor["device_class"] = deviceClass;
            }

            if (unit != null)
            {
                sensor["unit_of_measurement"] = unit;
            }

            if (stateClass != null)
            {
                sensor["state_class"] = stateClass;
            }

            if (options != null)
            {
                sensor["options"] = options;
            }

            if (icon != null)
            {
                sensor["icon"] = icon;
            }

            return sensor;
        }

        private async Task ClearDiscoveryAsync(IMqttClient client, string topic, CancellationToken cancellationToken)
        {
            await client.PublishAsync(MqttGatewayMqtt.BuildMessage(topic, string.Empty, MqttGatewayQos.AtLeastOnce, true), cancellationToken).ConfigureAwait(false);
        }

        private string GetDiscoveryTopic()
        {
            return $"{_settings.DiscoveryPrefix.Trim('/')}/device/{_settings.InstanceId}/config";
        }

        private string GetConfigurationUrl()
        {
            var host = _configFileProvider.BindAddress?.Trim();
            if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "0.0.0.0" || host == "::")
            {
                host = "localhost";
            }

            var urlBase = _configFileProvider.UrlBase?.Trim('/');
            var path = string.IsNullOrWhiteSpace(urlBase)
                ? "/settings/connect"
                : $"/{urlBase}/settings/connect";
            var scheme = _configFileProvider.EnableSsl ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var port = _configFileProvider.EnableSsl ? _configFileProvider.SslPort : _configFileProvider.Port;
            return new UriBuilder(scheme, host, port, path).Uri.ToString();
        }

        private enum WorkKind
        {
            Event,
            Snapshot,
            HomeAssistantBirth,
            Reconnect
        }

        private sealed class WorkItem
        {
            public WorkItem(WorkKind kind, MqttGatewayEvent gatewayEvent = null)
            {
                Kind = kind;
                Event = gatewayEvent;
            }

            public WorkKind Kind { get; }
            public MqttGatewayEvent Event { get; }
        }
    }
}
