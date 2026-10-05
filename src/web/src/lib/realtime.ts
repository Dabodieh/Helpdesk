import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useSyncExternalStore } from 'react'
import { csrfHeaders } from '../api/http'

/**
 * Realtime (SignalR) is an accelerator, never the source of truth: an event only says "something about ticket X changed" (ids and a
 * kind, no content) and the client re-reads through the authorised REST API. When the connection is not live, polling stays at its
 * normal cadence; when it is live, polling slows down to a reconciliation heartbeat.
 */
export type RealtimeStatus = 'off' | 'connecting' | 'live' | 'reconnecting' | 'offline'

export type TicketChange = { ticketId: string; departmentId: string; reference: string; kind: string; actorUserId: string | null; at: string }

export const POLL_FAST_MS = 30_000
export const POLL_SLOW_MS = 120_000

let status: RealtimeStatus = 'off'
const statusListeners = new Set<() => void>()
let remote: Record<string, TicketChange> = {}
const remoteListeners = new Set<() => void>()

function setStatus(next: RealtimeStatus) {
  if (status === next) return
  status = next
  statusListeners.forEach((l) => l())
}

/** Polling interval for live-ish queries: a slow heartbeat while the socket is live, the normal cadence otherwise. */
export const pollInterval = () => (status === 'live' ? POLL_SLOW_MS : POLL_FAST_MS)

export function useRealtimeStatus(): RealtimeStatus {
  return useSyncExternalStore(
    (l) => {
      statusListeners.add(l)
      return () => statusListeners.delete(l)
    },
    () => status,
  )
}

/** The latest change to this ticket made by SOMEONE ELSE (for a gentle "updated" notice; own changes are not announced). */
export function useRemoteChange(ticketId: string | undefined): TicketChange | null {
  return useSyncExternalStore(
    (l) => {
      remoteListeners.add(l)
      return () => remoteListeners.delete(l)
    },
    () => (ticketId ? (remote[ticketId] ?? null) : null),
  )
}

export function clearRemoteChange(ticketId: string) {
  if (!(ticketId in remote)) return
  const { [ticketId]: _removed, ...rest } = remote
  remote = rest
  remoteListeners.forEach((l) => l())
}

/** Applies one event: refresh counts, lists and the open ticket; remember foreign changes for the notice. Exported for tests. */
export function applyTicketChange(qc: QueryClient, change: TicketChange, myUserId: string) {
  void qc.invalidateQueries({ queryKey: ['tickets'] })
  void qc.invalidateQueries({ queryKey: ['ticket'] })
  if (change.actorUserId !== myUserId) {
    remote = { ...remote, [change.ticketId]: change }
    remoteListeners.forEach((l) => l())
  }
}

export type ConnectionFactory = () => Promise<HubConnection>

let factory: ConnectionFactory | null =
  import.meta.env.MODE === 'test'
    ? null
    : async () =>
        new HubConnectionBuilder()
          .withUrl('/hubs/tickets', { headers: await csrfHeaders() })
          .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
          .configureLogging(LogLevel.Warning)
          .build()

/** Test seam: inject a fake connection (or null to disable realtime). */
export function setConnectionFactory(next: ConnectionFactory | null) {
  factory = next
}

export function useRealtime(enabled: boolean, myUserId: string) {
  const qc = useQueryClient()
  useEffect(() => {
    if (!enabled || !factory) {
      setStatus('off')
      return
    }
    let stopped = false
    let connection: HubConnection | null = null
    let retry: ReturnType<typeof setTimeout> | undefined
    const make = factory

    async function start() {
      setStatus('connecting')
      try {
        connection = await make()
        connection.on('ticketChanged', (change: TicketChange) => applyTicketChange(qc, change, myUserId))
        connection.onreconnecting(() => setStatus('reconnecting'))
        connection.onreconnected(() => {
          setStatus('live')
          // Anything missed while disconnected is reconciled through REST.
          void qc.invalidateQueries({ queryKey: ['tickets'] })
          void qc.invalidateQueries({ queryKey: ['ticket'] })
        })
        connection.onclose(() => {
          if (stopped) return
          setStatus('offline')
          retry = setTimeout(() => void start(), 15_000)
        })
        await connection.start()
        if (connection.state === HubConnectionState.Connected) setStatus('live')
      } catch {
        if (stopped) return
        setStatus('offline')
        retry = setTimeout(() => void start(), 15_000)
      }
    }
    void start()
    return () => {
      stopped = true
      if (retry) clearTimeout(retry)
      void connection?.stop()
      setStatus('off')
    }
  }, [enabled, myUserId, qc])
}
