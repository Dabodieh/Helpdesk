import { QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { AppRoutes } from './App'
import { createQueryClient } from './queryClient'
import { resetCsrf } from './api/http'
import type { Me } from './api/schemas'

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
    const queue = queues.get(`${method} ${path}`)
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
