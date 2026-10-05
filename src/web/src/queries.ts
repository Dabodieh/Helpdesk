import { useQuery } from '@tanstack/react-query'
import { getQueueCounts } from './api/tickets'

/** Polling cadence for live-ish views (SignalR is Phase 4). */
export const POLL_MS = 30_000

export const livePolling = { refetchInterval: POLL_MS, refetchOnWindowFocus: true } as const

/** While an email is queued the conversation checks back quickly so "Queued" turns into "Sent"/"Failed" without waiting for the slow poll. */
export const PENDING_POLL_MS = 3_000

export function useQueueCounts(enabled: boolean) {
  return useQuery({ queryKey: ['tickets', 'counts'], queryFn: getQueueCounts, enabled, ...livePolling })
}
