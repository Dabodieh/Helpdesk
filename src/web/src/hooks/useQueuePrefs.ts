import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getQueuePrefs, putQueuePrefs, QUEUE_COLUMNS, type QueueColumn, type QueuePrefs, type QueuePrefsInput } from '../api/queuePrefs'

const key = ['queue-preferences']

/** The columns to show, in order: the saved choice (unknown keys dropped), else the standard set. */
export function visibleColumns(prefs: QueuePrefs | undefined): QueueColumn[] {
  const saved = (prefs?.columns ?? []).filter((c): c is QueueColumn => (QUEUE_COLUMNS as readonly string[]).includes(c))
  return prefs?.columns ? saved : [...QUEUE_COLUMNS]
}

/** The caller's own queue preferences (stored per account). Failing to load them just means the standard queue. */
export function useQueuePrefs() {
  const qc = useQueryClient()
  const query = useQuery({ queryKey: key, queryFn: getQueuePrefs, staleTime: 60_000, retry: false })
  const save = useMutation({
    mutationFn: (body: QueuePrefsInput) => putQueuePrefs(body),
    onSuccess: (data) => qc.setQueryData(key, data),
  })
  /** Saves a change on top of the current preferences (the server replaces the whole record). */
  const update = (patch: QueuePrefsInput, onSuccess?: () => void) =>
    save.mutate({ columns: query.data?.columns ?? null, defaultQueue: query.data?.defaultQueue ?? null, defaultViewId: query.data?.defaultViewId ?? null, ...patch }, { onSuccess })
  return { prefs: query.data, isPending: query.isPending, save, update }
}
