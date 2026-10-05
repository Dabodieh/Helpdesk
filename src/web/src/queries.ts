import { useQuery } from '@tanstack/react-query'
import { getQueueCounts } from './api/tickets'

/** Polling cadence for live-ish views (SignalR is Phase 4). */
export const POLL_MS = 30_000

export const livePolling = { refetchInterval: POLL_MS, refetchOnWindowFocus: true } as const

export function useQueueCounts(enabled: boolean) {
  return useQuery({ queryKey: ['tickets', 'counts'], queryFn: getQueueCounts, enabled, ...livePolling })
}
