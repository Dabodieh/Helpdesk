import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useRef, useSyncExternalStore } from 'react'
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

// ----- presence: ephemeral and advisory ("who else is on this ticket"). A state only: never text, keystrokes or history. -----

export type PresenceState = 'viewing' | 'reply' | 'note'
export type PresenceUser = { userId: string; displayName: string; state: PresenceState }

/** The server expires an entry after 60 s without a heartbeat. */
export const PRESENCE_HEARTBEAT_MS = 20_000

let hub: HubConnection | null = null
let presence: Record<string, PresenceUser[]> = {}
let declared: { ticketId: string; state: PresenceState } | null = null
const presenceListeners = new Set<() => void>()

function emitPresence() {
  presenceListeners.forEach((l) => l())
}

function send(ticketId: string, state: PresenceState | null) {
  if (hub?.state !== HubConnectionState.Connected) return
  hub.invoke('SetPresence', ticketId, state).catch(() => undefined) // advisory: a failed heartbeat is simply retried by the next one
}

/** Applied from the hub; exported for tests. */
export function applyPresence(message: { ticketId: string; users: PresenceUser[] }) {
  presence = { ...presence, [message.ticketId]: message.users }
  emitPresence()
}

/** The OTHER people on this ticket (the server never echoes the caller and already hides what the caller may not know). */
export function usePresence(ticketId: string | undefined): PresenceUser[] {
  return useSyncExternalStore(
    (l) => {
      presenceListeners.add(l)
      return () => presenceListeners.delete(l)
    },
    () => (ticketId ? (presence[ticketId] ?? EMPTY) : EMPTY),
  )
}
const EMPTY: PresenceUser[] = []

/** Tells the server what this tab is doing on the ticket (heartbeat every 20 s while it stays true); null or unmount leaves. Safe without a live connection. */
export function useReportPresence(ticketId: string | undefined, state: PresenceState | null) {
  const last = useRef<string | undefined>(undefined)
  useEffect(() => {
    if (!ticketId || !state) {
      if (declared) {
        send(declared.ticketId, null)
        declared = null
      }
      last.current = undefined
      return
    }
    declared = { ticketId, state }
    last.current = ticketId
    send(ticketId, state)
    const timer = setInterval(() => send(ticketId, state), PRESENCE_HEARTBEAT_MS)
    return () => clearInterval(timer)
  }, [ticketId, state])
  useEffect(
    () => () => {
      if (declared && declared.ticketId === last.current) {
        send(declared.ticketId, null)
        declared = null
      }
    },
    [],
  )
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

/** Everything that live events keep fresh is re-read through REST. Called after EVERY way back to a live connection (automatic reconnect and a manual restart). */
export function reconcile(qc: QueryClient) {
  for (const key of ['tickets', 'ticket', 'notifications', 'portal']) void qc.invalidateQueries({ queryKey: [key] })
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
        hub = connection
        connection.on('ticketChanged', (change: TicketChange) => applyTicketChange(qc, change, myUserId))
        connection.on('presence', applyPresence)
        // Requester portal: a reference and a coarse kind only; the portal re-reads through its own authorised API.
        connection.on('requestChanged', () => void qc.invalidateQueries({ queryKey: ['portal'] }))
        // An empty prompt: the notification list is re-read through the authorised REST API (and polling covers a missed prompt).
        connection.on('notificationsChanged', () => void qc.invalidateQueries({ queryKey: ['notifications'] }))
        connection.onreconnecting(() => setStatus('reconnecting'))
        connection.onreconnected(() => {
          setStatus('live')
          reconcile(qc)
          if (declared) send(declared.ticketId, declared.state) // the server forgot this connection: say again what we are doing
        })
        connection.onclose(() => {
          if (stopped) return
          setStatus('offline')
          retry = setTimeout(() => void start(), 15_000)
        })
        await connection.start()
        if (connection.state === HubConnectionState.Connected) {
          setStatus('live')
          reconcile(qc) // a (re)start after a long outage: whatever was missed is re-read
          if (declared) send(declared.ticketId, declared.state)
        }
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
      if (hub === connection) hub = null
      presence = {}
      setStatus('off')
    }
  }, [enabled, myUserId, qc])
}
