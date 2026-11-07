import { QueryClient } from '@tanstack/react-query';

/**
 * Singleton QueryClient for the application.
 *
 * Defaults chosen for a long-running Servarr-style desktop/tab app:
 * - staleTime 30s: avoids refetching data that was just fetched,
 *   reducing API hammering while the user is actively navigating.
 * - refetchOnWindowFocus false: the app runs in a browser tab for hours;
 *   we do NOT want a storm of refetches every time the user alt-tabs back.
 * - retry 1: one automatic retry for transient network blips, then surface
 *   the error to the UI. More retries would delay error feedback.
 * - refetchOnReconnect true: if the device comes back online after being
 *   offline, do refresh to pick up any changes that happened while away.
 */
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30 * 1000, // 30 seconds
      refetchOnWindowFocus: false,
      retry: 1,
      refetchOnReconnect: true,
    },
  },
});

export default queryClient;
