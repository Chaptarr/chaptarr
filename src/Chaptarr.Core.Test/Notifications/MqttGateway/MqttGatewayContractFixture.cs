using System;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Notifications.MqttGateway;

namespace Chaptarr.Core.Test.Notifications.MqttGateway
{
    [TestFixture]
    public class MqttGatewayContractFixture
    {
        [Test]
        public void should_serialize_canonical_event_fields_in_camel_case_and_omit_unrelated_fields()
        {
            var occurredAt = new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc);
            var gatewayEvent = MqttGatewayEvent.Create(
                "book_imported",
                "instance-1",
                "Chaptarr",
                new Author { Id = 42, Name = "Example Author" },
                new Book { Id = 123, Title = "Example Book", MediaType = BookMediaType.Audiobook },
                isUpgrade: false,
                correlationId: "correlation-1",
                eventId: "event-1",
                occurredAt: occurredAt);

            using var document = JsonDocument.Parse(gatewayEvent.ToJson());
            var root = document.RootElement;

            Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("event").GetString(), Is.EqualTo("book_imported"));
            Assert.That(root.GetProperty("eventId").GetString(), Is.EqualTo("event-1"));
            Assert.That(root.GetProperty("correlationId").GetString(), Is.EqualTo("correlation-1"));
            Assert.That(root.GetProperty("occurredAt").GetDateTime(), Is.EqualTo(occurredAt));
            Assert.That(root.GetProperty("instance").GetProperty("id").GetString(), Is.EqualTo("instance-1"));
            Assert.That(root.GetProperty("author").GetProperty("name").GetString(), Is.EqualTo("Example Author"));
            Assert.That(root.GetProperty("book").GetProperty("mediaType").GetString(), Is.EqualTo("audiobook"));
            Assert.That(root.GetProperty("isUpgrade").GetBoolean(), Is.False);
            Assert.That(root.TryGetProperty("stage", out _), Is.False);
        }

        [Test]
        public void should_build_event_topics_from_the_instance_scoped_prefix()
        {
            var settings = new MqttGatewaySettings
            {
                TopicPrefix = "library",
                InstanceId = "instance-1"
            };

            Assert.That(MqttGatewayMqtt.GetEventTopic(settings, "book_added"), Is.EqualTo("library/instance-1/book_added"));
        }

        [Test]
        public void should_fingerprint_mqtt_settings_consistently_and_detect_changes()
        {
            var settings = new MqttGatewaySettings
            {
                Host = "broker.example",
                InstanceId = "instance-1"
            };
            var sameSettings = new MqttGatewaySettings
            {
                Host = "broker.example",
                InstanceId = "instance-1"
            };
            var changedSettings = new MqttGatewaySettings
            {
                Host = "broker.example",
                InstanceId = "instance-1",
                Port = 1884
            };

            Assert.That(MqttGatewayMqtt.GetSettingsFingerprint(settings), Is.EqualTo(MqttGatewayMqtt.GetSettingsFingerprint(sameSettings)));
            Assert.That(MqttGatewayMqtt.GetSettingsFingerprint(settings), Is.Not.EqualTo(MqttGatewayMqtt.GetSettingsFingerprint(changedSettings)));
        }

        [Test]
        public void should_ignore_snapshot_generation_time_in_the_sensor_state_fingerprint()
        {
            var snapshot = new MqttGatewaySnapshot
            {
                GeneratedAt = new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc),
                BooksTotal = 12,
                Health = "ok"
            };

            var laterSnapshot = snapshot with { GeneratedAt = snapshot.GeneratedAt.AddMinutes(5) };
            var changedSnapshot = snapshot with { BooksTotal = 13 };

            Assert.That(laterSnapshot.StateFingerprint(), Is.EqualTo(snapshot.StateFingerprint()));
            Assert.That(changedSnapshot.StateFingerprint(), Is.Not.EqualTo(snapshot.StateFingerprint()));
        }
    }
}
