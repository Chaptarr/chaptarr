import { useEffect } from 'react';
import { useDispatch } from 'react-redux';
import { batchActions } from 'redux-batched-actions';
import useApiQuery from 'Store/Hooks/useApiQuery';
import { update, set } from 'Store/Actions/baseActions';

/**
 * Thunk-hydration pattern: fetch data via react-query, then push the result
 * into Redux so existing selectors/consumers keep working unchanged.
 *
 * This is the migration template for converting thunk-backed read endpoints
 * to react-query. The thunk remains in place for other consumers; this hook
 * can coexist with it. Once ALL consumers are migrated to react-query, the
 * thunk's fetch path can be removed.
 *
 * Usage (in a functional component):
 *   const { data: tags } = useTagsHydration();
 *   // tags is also in Redux store at state.tags.items via the hydration effect.
 */
export default function useTagsHydration() {
  const dispatch = useDispatch();

  const result = useApiQuery(
    ['tags'],
    { url: '/tag' },
    {
      // Tags rarely change — cache for 5 minutes to avoid redundant fetches
      // when multiple pages mount simultaneously.
      staleTime: 5 * 60 * 1000,
    }
  );

  // Hydrate Redux whenever data arrives or changes.
  // This mirrors what createFetchHandler('tags', '/tag') does:
  //   dispatch(update({ section: 'tags', data }))
  //   dispatch(set({ section: 'tags', isFetching: false, isPopulated: true, error: null }))
  useEffect(() => {
    if (result.data !== undefined) {
      dispatch(batchActions([
        update({ section: 'tags', data: result.data }),
        set({
          section: 'tags',
          isFetching: false,
          isPopulated: true,
          error: null,
        }),
      ]));
    }
  }, [result.data, dispatch]);

  return result;
}
