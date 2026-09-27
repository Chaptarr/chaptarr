import { useQuery, UseQueryOptions } from '@tanstack/react-query';
import createAjaxRequest from 'Utilities/createAjaxRequest';

/**
 * Options that map to createAjaxRequest parameters.
 */
export interface ApiQueryParams {
  /** The URL path (relative to the API root), e.g. '/tag' or '/system/status' */
  url: string;
  /** HTTP method (default: 'GET') */
  method?: string;
  /** Request body data */
  data?: Record<string, unknown>;
  /** Expected response type (default: 'json') */
  dataType?: string;
  /** Content-Type header value */
  contentType?: string | false;
  /** Request timeout in ms */
  timeout?: number;
  /** Whether to process data (default: true) */
  processData?: boolean;
  /** Traditional query-string serialization (default: false) */
  traditional?: boolean;
}

/**
 * Wraps createAjaxRequest for use with @tanstack/react-query.
 *
 * Signature mirrors useQuery but accepts ajax-style params instead of queryFn:
 *   useApiQuery(queryKey, ajaxParams, reactQueryOptions?)
 *
 * The queryKey must include enough info to uniquely identify the request
 * (typically [endpoint, params]). The hook calls createAjaxRequest inside
 * queryFn and returns the resulting promise.
 *
 * Abort: react-query v4 provides no AbortSignal to queryFn by default.
 * The request will NOT be cancelled on unmount in this implementation.
 * This is acceptable for most read endpoints in this app (responses are
 * small JSON payloads). For long-running requests, add `signal` support
 * manually or use queryClient.cancelQueries().
 */
export default function useApiQuery<TData = unknown>(
  queryKey: unknown[],
  ajaxParams: ApiQueryParams,
  queryOptions?: Omit<UseQueryOptions<TData, Error, TData, unknown[]>, 'queryKey' | 'queryFn'>
) {
  return useQuery<TData, Error, TData, unknown[]>({
    queryKey,
    queryFn: () => {
      const { request } = createAjaxRequest({
        method: 'GET',
        dataType: 'json',
        ...ajaxParams,
      });
      return request.then((data: TData) => data);
    },
    ...queryOptions,
  });
}
