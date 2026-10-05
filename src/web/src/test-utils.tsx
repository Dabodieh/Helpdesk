import { QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { AppRoutes } from './App'
import { createQueryClient } from './queryClient'
import { resetCsrf } from './api/http'
import type { Me } from './api/schemas'
import type { Message, TicketDetail, TicketListItem } from './api/ticketSchemas'

export type Call = { method: string; path: string; headers: Record<string, string>; body: unknown }
type Handler = (call: Call) => Response | unknown
type Handlers = Record<string, Handler | Handler[]>

export function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}
export const problem = (status: number, title: string, extra: object = {}) => json({ status, title, ...extra }, status)

/** Stub fetch with handlers keyed "METHOD /path" (query string included when present). Arrays are consumed in order, last repeats. */
export function mockFetch(handlers: Handlers) {
  const calls: Call[] = []
  const queues = new Map<string, Handler[]>()
  for (const [k, v] of Object.entries(handlers)) queues.set(k, Array.isArray(v) ? [...v] : [v])
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const path = String(input)
    const call: Call = {
      method,
      path,
      headers: (init?.headers ?? {}) as Record<string, string>,
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
    }
    calls.push(call)
    // Exact match including the query string first, then the bare path.
    const queue = queues.get(`${method} ${path}`) ?? queues.get(`${method} ${path.split('?')[0]}`)
    if (!queue) return problem(404, 'Not Found')
    const h = queue.length > 1 ? queue.shift()! : queue[0]
    const out = h(call)
    return out instanceof Response ? out : json(out)
  })
  vi.stubGlobal('fetch', fn)
  return calls
}

export const csrfHandler = { 'GET /api/csrf': () => ({ headerName: 'X-CSRF-TOKEN', token: 'tok' }) }

export function makeMe(over: Partial<Me> = {}): Me {
  return {
    id: 'u1',
    displayName: 'Test User',
    email: 'u@example.test',
    isPlatformAdmin: false,
    platformPermissions: [],
    memberships: [],
    ...over,
  }
}

export function renderApp(route = '/') {
  resetCsrf()
  const client = createQueryClient()
  client.setDefaultOptions({ queries: { retry: false } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <AppRoutes />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export const DEPT = {
  id: 'd1',
  key: 'IT',
  name: 'IT Support',
  description: 'Desk',
  isActive: true,
  myRole: 'DepartmentAdmin',
  version: 'v1',
}

export const AGENT_PERMS = [
  'department.read',
  'tickets.read',
  'tickets.create',
  'tickets.reply',
  'tickets.note',
  'tickets.notes.read',
  'tickets.update',
  'tickets.assign.self',
]
export const membership = (permissions: string[], role = 'Agent') => ({
  departmentId: 'd1',
  departmentKey: 'IT',
  departmentName: 'IT Support',
  role,
  permissions,
})
export const agentMe = () => makeMe({ memberships: [membership(AGENT_PERMS)] })
export const leadMe = () => makeMe({ memberships: [membership([...AGENT_PERMS, 'tickets.assign.any'], 'TeamLead')] })
export const viewerMe = () => makeMe({ memberships: [membership(['department.read', 'tickets.read', 'tickets.notes.read'], 'Viewer')] })

export function makeTicket(over: Partial<TicketListItem> = {}): TicketListItem {
  return {
    id: 't1',
    reference: 'IT-1',
    departmentId: 'd1',
    departmentKey: 'IT',
    subject: 'Printer jam',
    status: 'Open',
    priority: 'Normal',
    requester: { id: 'r1', displayName: 'Rita Requester', email: 'rita@example.test' },
    team: null,
    assignee: null,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T10:00:00Z',
    ...over,
  }
}

export function makeDetail(over: Partial<TicketDetail> = {}): TicketDetail {
  return {
    ...makeTicket(),
    departmentName: 'IT Support',
    resolvedAt: null,
    closedAt: null,
    version: 'v1',
    requester: { id: 'r1', displayName: 'Rita Requester', email: 'rita@example.test', openTicketCount: 2, totalTicketCount: 5 },
    ...over,
  }
}

export function makeMessage(over: Partial<Message> = {}): Message {
  return {
    id: 'm1',
    ticketId: 't1',
    kind: 'Public',
    authorType: 'Requester',
    author: { id: null, displayName: 'Rita Requester' },
    body: 'The printer is jammed',
    source: 'manual',
    deliveryState: 'None',
    createdAt: '2026-10-01T09:00:00Z',
    ...over,
  }
}

export const page = <T,>(items: T[], nextCursor: string | null = null) => ({ items, nextCursor })

/** Routes every workspace screen needs; spread first, override per test. */
export const workspaceRoutes = (me: Me = agentMe()) => ({
  'GET /api/me': () => me,
  'GET /api/tickets/queue-counts': () => ({ my: 3, team: 1, unassigned: 4, all: 9 }),
  'GET /api/tickets': () => page([makeTicket()]),
  'GET /api/departments/d1/teams': () => [],
  'GET /api/departments/d1/members': () => [],
  ...csrfHandler,
})
