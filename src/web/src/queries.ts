import { useQuery } from '@tanstack/react-query'
import { getQueueCounts } from './api/tickets'
import { pollInterval } from './lib/realtime'

/** Live-ish views: SignalR pushes changes; polling remains as reconciliation (slow while the socket is live, normal otherwise). */
export const livePolling = { refetchInterval: () => pollInterval(), refetchOnWindowFocus: true } as const

/** While an email is queued the conversation checks back quickly so "Queued" turns into "Sent"/"Failed" without waiting for the slow poll. */
export const PENDING_POLL_MS = 3_000

export function useQueueCounts(enabled: boolean) {
  return useQuery({ queryKey: ['tickets', 'counts'], queryFn: getQueueCounts, enabled, ...livePolling })
}
