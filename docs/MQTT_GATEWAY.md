# MQTT Gateway

The MQTT Gateway publishes Chaptarr library state and lifecycle events to an
MQTT broker. Add it under **Settings → Connect**. Home Assistant discovery is
optional; MQTT topics and the event schema are usable by other consumers too.

## Connection settings

Set the broker host and port, then enable TLS when the broker supports it.
Broker certificates are validated by default. The advanced **Allow Invalid TLS
Certificates** option disables that validation and should only be used with a
trusted private broker. The username and password use the normal connection
secret masking and backup behavior.

Each connection gets a stable instance ID. Chaptarr appends it to the topic
prefix so multiple instances can share a broker without publishing to the same
topics. With the default prefix, a connection uses topics under
`chaptarr/<instance-id>/`. Keep the instance ID stable after Home Assistant has
registered the entities. The MQTT client ID is generated from that ID unless a
custom client ID is supplied.

Sensor publishing, event publishing, and Home Assistant discovery can be
enabled independently. Sensor refresh defaults to five minutes and the
upcoming-release window defaults to 30 days. Events use QoS 1 by default and
are not retained. The **Test** action connects with a temporary client ID and
publishes a `test` event; it does not publish sensor data or availability.

## Topics and snapshot

The gateway publishes the current JSON sensor snapshot to:

```text
chaptarr/<instance-id>/state
```

It publishes retained `online` / `offline` availability to
`chaptarr/<instance-id>/status`. The connection configures `offline` as its
Last Will, so consumers can mark entities unavailable after an unclean broker
disconnect.

The snapshot contains `schemaVersion`, `generatedAt`, `books_total`,
`audiobooks_total`, `missing_books`, `wanted_books`, `queue`,
`upcoming_releases`, `library_size`, `last_import`, and `health`. Missing and
wanted counts include monitored released books only. Wanted is the distinct
union of missing books and books below their configured quality cutoff.
`library_size` is in bytes, `last_import` is a UTC timestamp or `null`, and
health is `ok`, `warning`, or `error`.

Snapshots publish on connect and reconnect, after relevant library or queue
changes with a short debounce, and at the configured interval. A snapshot is
sent only when a sensor value changes, except for the forced startup,
reconnect, and Home Assistant birth-message refreshes. State messages are not
retained; Home Assistant restores sensor state itself, and Chaptarr refreshes
it after Home Assistant announces that its MQTT integration is online.

## Home Assistant discovery

When enabled, Chaptarr publishes one retained device-discovery document for
the nine sensors. Their stable unique IDs include the connection's instance
ID, while their initial entity IDs use the requested `sensor.chaptarr_*`
names. All sensors share the snapshot state topic and availability topic.
Counts use measurement state classes, library size uses the data-size class
and byte units, last import uses the timestamp class, and health uses the enum
class with `ok`, `warning`, and `error` options.

Discovery configuration and availability are retained; sensor state is not.
Chaptarr republishes discovery after Home Assistant's birth message with a
small randomized delay. Disabling discovery, disabling the connection, or
removing it clears the retained discovery document when the broker is
reachable. A normal Chaptarr shutdown leaves discovery in place so entities
survive restarts.

## Lifecycle events

Events publish to `chaptarr/<instance-id>/<event-name>`:

```text
book_added
book_downloaded
book_imported
book_upgraded
book_failed
new_release_detected
series_completed
```

Each payload is a versioned JSON envelope with `schemaVersion`, `event`,
`eventId`, `correlationId`, `occurredAt`, and stable instance information.
Relevant author, book, download, series, upgrade, and failure-stage fields are
included when available; filesystem paths and credentials are not included.
An upgrade emits both `book_imported` and `book_upgraded` with the same
correlation ID. Download and import failures identify their stage. Initial
author/catalog imports do not produce `new_release_detected` events, and a
series completion is emitted only for a first import that completes all
released, monitored primary books of that series and media type.

Delivery runs outside Chaptarr's primary event handlers, uses a bounded queue,
and reconnects with backoff and jitter. Events are not retained. If the queue
fills during a prolonged outage, new events can be dropped and the log records
that condition. MQTT QoS and reconnection may result in duplicate deliveries;
consumers can deduplicate with `eventId`.

## Canonical Webhook events

Existing Webhook connections can opt into **Include Canonical Events**. Each
canonical lifecycle event is sent as a separate request with the same JSON
envelope used by MQTT and an `X-Chaptarr-Event` header. Existing webhook
payloads, event types, and trigger settings remain unchanged. If both canonical
delivery and legacy triggers are enabled, a lifecycle change can produce both
the canonical request and its existing legacy notification.
