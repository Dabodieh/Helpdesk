import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, expect, test, vi } from 'vitest'
import { setConnectionFactory, useRealtime } from './realtime'

afterEach(() => setConnectionFactory(null))

function fakeConnection() {
  const handlers: { reconnected?: () => void; closed?: () => void } = {}
  const connection = {
    state: HubConnectionState.Connected,
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: (cb: () => void) => void (handlers.reconnected = cb),
    onclose: (cb: () => void) => void (handlers.closed = cb),
    start: vi.fn(async () => undefined),
    stop: vi.fn(async () => undefined),
    invoke: vi.fn(async () => undefined),
  }
  return { connection: connection as unknown as HubConnection, handlers }
}

function setup() {
  const qc = new QueryClient()
  const invalidate = vi.spyOn(qc, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  return { invalidate, wrapper }
}

const keys = (invalidate: ReturnType<typeof vi.spyOn>) => invalidate.mock.calls.map((c) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey))

test('every way back to a live connection re-reads tickets, notifications and the portal', async () => {
  const fake = fakeConnection()
  setConnectionFactory(async () => fake.connection)
  const { invalidate, wrapper } = setup()
  renderHook(() => useRealtime(true, 'u1'), { wrapper })

  // A (re)start that succeeds after an outage re-reads what was missed, portal included.
  await waitFor(() => expect(keys(invalidate)).toContain('["portal"]'))
  invalidate.mockClear()

  // And so does the automatic reconnect.
  fake.handlers.reconnected?.()
  expect(keys(invalidate)).toEqual(expect.arrayContaining(['["tickets"]', '["ticket"]', '["notifications"]', '["portal"]']))
})
