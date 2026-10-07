import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, csrfHandler, mockFetch, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const LIST = {
  unreadCount: 2,
  items: [
    { id: 'n1', type: 'assigned', createdAt: '2026-01-12T10:00:00Z', read: false, reference: 'IT-7', subject: 'Printer jam' },
    { id: 'n2', type: 'followed_update', createdAt: '2026-01-12T09:00:00Z', read: true, reference: 'IT-3', subject: 'VPN drops' },
  ],
}
const routes = (list: unknown) => ({ ...workspaceRoutes(agentMe()), ...csrfHandler, 'GET /api/notifications?limit=20': () => list })

test('the bell shows the unread count and a read/unread list that opens the ticket and marks it read', async () => {
  const calls = mockFetch({ ...routes(LIST), 'POST /api/notifications/n1/read': () => new Response(null, { status: 204 }) })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: 'Notifications, 2 unread' }))
  const panel = await screen.findByRole('region', { name: 'Notifications' })
  expect(within(panel).getByText(/Assigned to you/)).toBeInTheDocument()
  expect(within(panel).getByText(/Update on a ticket you follow/)).toBeInTheDocument()
  expect(within(panel).getAllByRole('button', { name: /Mark IT-\d read/ })).toHaveLength(1) // only the unread one
  fireEvent.click(within(panel).getByText(/Assigned to you/))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/notifications/n1/read')).toBe(true))
})

test('mark all read, and a compact empty state', async () => {
  const calls = mockFetch({ ...routes(LIST), 'POST /api/notifications/read-all': () => new Response(null, { status: 204 }) })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: /Notifications, 2 unread/ }))
  fireEvent.click(await screen.findByRole('button', { name: 'Mark all read' }))
  await waitFor(() => expect(calls.some((c) => c.path === '/api/notifications/read-all')).toBe(true))
})

test('nothing to show', async () => {
  mockFetch(routes({ unreadCount: 0, items: [] }))
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: 'Notifications' }))
  expect(await screen.findByText(/Nothing needs your attention/)).toBeInTheDocument()
})
