import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { makeDetail, makeMessage, makeTicket, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const PROFILE = {
  id: 'r1',
  displayName: 'Rita Requester',
  email: 'rita@example.test',
  firstSeenAt: '2026-01-01T00:00:00Z',
  lastActivityAt: '2026-10-01T10:00:00Z',
  openCount: 2,
  resolvedCount: 3,
  totalCount: 5,
  open: [makeTicket({ id: 't1', reference: 'IT-1', subject: 'Printer jam' }), makeTicket({ id: 't2', reference: 'IT-2', subject: 'VPN drops', status: 'Pending' })],
  previous: [makeTicket({ id: 't3', reference: 'IT-3', subject: 'Password reset', status: 'Resolved' })],
}

const routes = () => ({
  ...workspaceRoutes(),
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/requesters/r1': () => PROFILE,
})

test('the requester name in the ticket header opens a drawer with counts, open and previous tickets and a link to the full history', async () => {
  mockFetch(routes())
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'Rita Requester' }))
  const drawer = await screen.findByRole('dialog', { name: 'Rita Requester' })
  expect(within(drawer).getByText('rita@example.test')).toBeInTheDocument()
  expect(within(drawer).getByText('2')).toBeInTheDocument()
  expect(within(drawer).getByRole('link', { name: /IT-2/ })).toHaveAttribute('href', '/tickets/IT-2')
  expect(within(within(drawer).getByRole('region', { name: 'Previous' })).getByRole('link', { name: /IT-3/ })).toBeInTheDocument()
  expect(within(drawer).getByRole('link', { name: 'View full history' })).toHaveAttribute('href', '/requesters/r1')
})

test('a requester with no other visible tickets gets a plain empty state', async () => {
  mockFetch({ ...routes(), 'GET /api/requesters/r1': () => ({ ...PROFILE, openCount: 1, resolvedCount: 0, totalCount: 1, open: [PROFILE.open[0]], previous: [] }) })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'Rita Requester' }))
  const drawer = await screen.findByRole('dialog', { name: 'Rita Requester' })
  expect(await within(drawer).findByText('No other requests from this person.')).toBeInTheDocument()
  expect(within(drawer).queryByRole('region', { name: 'Previous' })).not.toBeInTheDocument()
})

test('the history page filters by state on the server and pages with a cursor', async () => {
  const calls: string[] = []
  mockFetch({
    ...workspaceRoutes(),
    'GET /api/requesters/r1': () => PROFILE,
    'GET /api/requesters/r1/tickets': (c) => {
      calls.push(c.path)
      if (c.path.includes('state=resolved')) return page([PROFILE.previous[0]])
      return c.path.includes('cursor=next1') ? page([PROFILE.open[1]]) : page([PROFILE.open[0]], 'next1')
    },
  })
  renderApp('/requesters/r1')
  expect(await screen.findByRole('heading', { name: 'Rita Requester', level: 1 })).toBeInTheDocument()
  expect(await screen.findByRole('link', { name: /IT-1/ })).toHaveAttribute('href', '/tickets/IT-1')
  fireEvent.click(screen.getByRole('button', { name: 'Show more' }))
  expect(await screen.findByRole('link', { name: /IT-2/ })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('tab', { name: 'Resolved and closed' }))
  expect(await screen.findByRole('link', { name: /IT-3/ })).toBeInTheDocument()
  expect(calls.some((p) => p.includes('state=resolved'))).toBe(true)
  expect(screen.queryByRole('link', { name: /IT-1/ })).not.toBeInTheDocument()
})
