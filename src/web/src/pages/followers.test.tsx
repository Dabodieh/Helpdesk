import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, makeDetail, makeMessage, makeTicket, mockFetch, page, renderApp, viewerMe, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const ANTON = { userId: 'u1', displayName: 'Anton Bodell', addedAt: '2026-10-02T10:00:00Z' }
const SARAH = { userId: 'u2', displayName: 'Sarah Smith', addedAt: '2026-10-02T11:00:00Z' }

const routes = (me = agentMe()) => ({
  ...workspaceRoutes(me),
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
  'GET /api/tickets/t1/followers': () => ({ items: [SARAH], following: false }),
})

test('Follow becomes ✓ Following, unfollow is one click and only ever touches your own follow', async () => {
  const calls = mockFetch({
    ...routes(),
    'POST /api/tickets/t1/followers/me': () => ({ items: [SARAH, ANTON], following: true }),
    'DELETE /api/tickets/t1/followers/me': () => ({ items: [SARAH], following: false }),
  })
  renderApp('/queue/my/IT-1')
  const box = await screen.findByRole('region', { name: 'Followers' })
  expect(await within(box).findByText('Sarah Smith')).toBeInTheDocument()
  fireEvent.click(within(box).getByRole('button', { name: 'Follow' }))
  const on = await within(box).findByRole('button', { name: '✓ Following' })
  expect(on).toHaveAttribute('aria-pressed', 'true')
  expect(within(box).getByText('Anton Bodell')).toBeInTheDocument()
  fireEvent.click(on)
  expect(await within(box).findByRole('button', { name: 'Follow' })).toBeInTheDocument()
  expect(within(box).getByText('Sarah Smith')).toBeInTheDocument()
  expect(calls.filter((c) => c.method === 'DELETE').map((c) => c.path)).toEqual(['/api/tickets/t1/followers/me'])
})

test('an agent can add a colleague from the search and remove a follower; a read-only viewer can only follow', async () => {
  const calls = mockFetch({
    ...routes(),
    'GET /api/tickets/t1/follower-candidates?q=ant': () => [{ userId: 'u1', displayName: 'Anton Bodell', email: 'anton@example.test' }],
    'GET /api/tickets/t1/follower-candidates?q=': () => [],
    'POST /api/tickets/t1/followers/u1': () => ({ items: [SARAH, ANTON], following: false }),
    'DELETE /api/tickets/t1/followers/u2': () => ({ items: [ANTON], following: false }),
  })
  renderApp('/queue/my/IT-1')
  const box = await screen.findByRole('region', { name: 'Followers' })
  fireEvent.click(await within(box).findByRole('button', { name: '+ Add' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add follower' })
  fireEvent.change(within(dialog).getByLabelText('Search by name or email'), { target: { value: 'ant' } })
  fireEvent.click(await within(dialog).findByRole('button', { name: /Anton Bodell/ }))
  await vi.waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(within(box).getByText('Anton Bodell')).toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST' && c.path === '/api/tickets/t1/followers/u1')).toBe(true)

  fireEvent.click(within(box).getByRole('button', { name: 'Remove Sarah Smith as a follower' }))
  await vi.waitFor(() => expect(within(box).queryByText('Sarah Smith')).not.toBeInTheDocument())
})

test('without update permission there is no add or remove control, only the own follow toggle', async () => {
  mockFetch(routes(viewerMe()))
  renderApp('/queue/my/IT-1')
  const box = await screen.findByRole('region', { name: 'Followers' })
  expect(await within(box).findByRole('button', { name: 'Follow' })).toBeInTheDocument()
  expect(within(box).queryByRole('button', { name: '+ Add' })).not.toBeInTheDocument()
  expect(within(box).queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument()
})

test('a merged ticket shows no follow control', async () => {
  mockFetch({
    ...routes(),
    'GET /api/tickets/IT-1': () =>
      makeDetail({ mergedInto: { id: 't9', reference: 'IT-9', subject: 'Canonical', mergedAt: '2026-10-02T10:00:00Z', mergedBy: null } }),
  })
  renderApp('/queue/my/IT-1')
  await screen.findByRole('region', { name: 'Merged ticket' })
  expect(screen.queryByRole('region', { name: 'Followers' })).not.toBeInTheDocument()
})

test('the Following queue is in the navigation and asks the server for the following queue', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(),
    'GET /api/tickets/queue-counts': () => ({ my: 3, unassigned: 4, all: 9, following: 2 }),
    'GET /api/tickets': () => page([makeTicket({ id: 't7', reference: 'IT-7', subject: 'Followed one' })]),
  })
  renderApp('/queue/following')
  expect(await screen.findByText('Followed one')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Following/ })).toHaveAttribute('href', '/queue/following')
  expect(calls.some((c) => c.path.startsWith('/api/tickets?') && c.path.includes('queue=following'))).toBe(true)
})
