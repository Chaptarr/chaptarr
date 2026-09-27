/* eslint-disable no-restricted-globals */
// Chaptarr Service Worker — Workbox InjectManifest (runtime-caching only)
//
// ⚠ No precache: the app sits behind HTTP Basic Auth. SW install-time
// precache fetches are *not* authenticated (they don't carry the browser's
// cached credentials) → 401 → install fails → SW is discarded.
// Runtime caching is auth-safe because it intercepts the page's
// already-authenticated requests.  self.__WB_MANIFEST (injected by
// InjectManifest) is intentionally unused.
//
// This file is processed by workbox-webpack-plugin at build time.
// self.__WB_MANIFEST is injected by InjectManifest but intentionally unused
// (see "no precache" note above).
const _WB_MANIFEST = self.__WB_MANIFEST || [];

import { cleanupOutdatedCaches } from 'workbox-precaching';
import { registerRoute, NavigationRoute } from 'workbox-routing';
import { CacheFirst, NetworkFirst, NetworkOnly } from 'workbox-strategies';
import { ExpirationPlugin } from 'workbox-expiration';
import { CacheableResponsePlugin } from 'workbox-cacheable-response';
import { setCacheNameDetails } from 'workbox-core';

// ── Cache naming ──────────────────────────────────────────────
// Use a well-known prefix so chunkCleanup's cache wipe and any
// debugging remain predictable.  All workbox-managed caches will
// start with `chaptarr-`.
setCacheNameDetails({
  prefix: 'chaptarr',
  precache: 'precache-v2',
  runtime: 'runtime',
  googleAnalytics: 'ga',
});

// Remove old precache entries from previous workbox versions.
cleanupOutdatedCaches();

// ── Activation ───────────────────────────────────────────────
// skipWaiting + clients.claim ensure a freshly-deployed SW takes
// control immediately instead of waiting for the next navigation.
// This allows the SW to self-heal if chunkCleanup wiped caches.
self.skipWaiting();
self.clients.claim();

// ── WebSocket / SignalR — network only ──────────────────────
// Explicitly route ws/wss requests to NetworkOnly so SignalR
// WebSocket negotiation is never cached or intercepted.
registerRoute(
  ({ url }) => url.protocol === 'ws:' || url.protocol === 'wss:',
  new NetworkOnly()
);

// ── Navigation (SPA fallback) ───────────────────────────────
// Serve index.html for same-origin navigation requests that don't
// match an API/hub route.  Uses NetworkFirst so fresh HTML is
// preferred when the network is available — this avoids ChunkLoadError
// on first load after a deploy (a stale index.html referencing deleted
// [contenthash] chunks).
//
// NOTE — Sub-path deployment limitation:
// The precache manifest URLs use build-time publicPath '/' (e.g.
// '/runtime-abc.js').  This works correctly for the default root
// deployment (urlBase='/').  Deploying under a sub-path (urlBase≠'/')
// requires additional workbox configuration (e.g. a manifestTransforms
// that prefixes URLs, or runtime URL modification) and is a known
// limitation needing live-server testing.  Do NOT use modifyURLPrefix
// to strip '/' — that breaks matching for sub-path deployments and
// also corrupts matching for the default root case.
registerRoute(
  new NavigationRoute(
    new NetworkFirst({
      networkTimeoutSeconds: 3,
      cacheName: 'chaptarr-navigation',
      plugins: [
        new CacheableResponsePlugin({ statuses: [0, 200] }),
      ],
    }),
    {
      denylist: [/^\/api\//, /\/signalr/i, /\/hub/i],
    }
  )
);

// ── API routes — network only ────────────────────────────────
// All /api/* requests must always go to the network.  We also
// exclude SignalR hub endpoints to avoid caching WebSocket
// negotiation traffic.  Strict regexes prevent false positives
// (e.g. matching a path like /apiology).
registerRoute(
  ({ url }) =>
    /^\/api\//.test(url.pathname) ||
    /\/signalr/i.test(url.pathname) ||
    /\/hub/i.test(url.pathname),
  new NetworkOnly()
);

// ── Static asset runtime cache ────────────────────────────────
// Cache-first for same-origin static assets: JS, CSS, web fonts,
// and common image types.  These are content-hashed at build time
// so they never change in-place — cache-first is safe.
registerRoute(
  ({ url, request }) => {
    // Only cache same-origin requests (respects urlBase prefix).
    if (url.origin !== self.location.origin) return false;

    const ext = url.pathname.match(/\.(js|css|woff2?|ttf|otf|eot|svg|png|jpg|jpeg|gif|webp|ico)$/i);
    if (!ext) return false;

    // Skip the service worker itself.
    if (url.pathname.endsWith('/sw.js')) return false;

    return true;
  },
  new CacheFirst({
    cacheName: 'chaptarr-static-assets',
    plugins: [
      new CacheableResponsePlugin({ statuses: [0, 200] }),
      new ExpirationPlugin({
        maxEntries: 120,
        maxAgeSeconds: 30 * 24 * 60 * 60, // 30 days
      }),
    ],
  })
);
