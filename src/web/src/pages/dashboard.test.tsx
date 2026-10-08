import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { AGENT_PERMS, csrfHandler, json, makeMe, makeTicket, membership, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const DASH = {
  departmentId: 'd1',
  departmentName: 'IT Support',
  generatedAt: '2026-01-12T13:30:00Z',
  backlog: { open: 4, pending: 2, unassigned: 3, awaitingFirstResponse: 1 },
  sla: { overdue: 2, dueSoon: 1, dueSoonWindowMinutes: 240 },
  ageing: [
    { key: 'lt1d', label: 'Under 1 day', count: 3 },
    { key: 'gt14d', label: 'Over 2 weeks', count: 1 },
  ],
  oldest: [{ reference: 'IT-7', subject: 'Printer jam', status: 'Open', priority: 'High', createdAt: '2026-01-01T09:00:00Z', assignee: null, slaOverdue: true }],
  agents: [
    { userId: 'u1', name: 'Ann Agent', total: 4, overdue: 1 },
    { userId: null, name: 'Unassigned', total: 2, overdue: 1 },
  ],
  sends: { failedTickets: 0, uncertainTickets: 1 },
  mailboxes: [{ mailboxId: 'mb1', address: 'desk@helpdesk.test', displayName: 'Desk', status: 'Attention', reasons: ['outbound_uncertain'], outboundFailed: 0, outboundUncertain: 1 }],
}

const VOLUME = {
  departmentId: 'd1',
  from: '2026-01-06',
  to: '2026-01-12',
  received: 5,
  resolved: 3,
  daily: [
    { date: '2026-01-10', received: 2, resolved: 0 },
    { date: '2026-01-11', received: 1, resolved: 1 },
    { date: '2026-01-12', received: 2, resolved: 2 },
  ],
}
const overdueTicket = makeTicket({ id: 't9', reference: 'IT-9', subject: 'VPN down', priority: 'High', sla: { state: 'Breached', dueAt: '2026-01-12T08:00:00Z', firstResponse: 'Met', resolution: 'Breached' } })

const routes = (dash: unknown, extra: Record<string, unknown> = {}) => ({
  ...workspaceRoutes(),
  ...csrfHandler,
  'GET /api/departments/d1/dashboard': () => dash,
  'GET /api/departments/d1/reports/volume': () => VOLUME,
  'GET /api/tickets': (c: { path: string }) => json(page(c.path.includes('sla=breached') ? [overdueTicket] : [])),
  ...extra,
})

test('the dashboard shows the headline figures as cards and every figure links into the existing queue', async () => {
  mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  expect(await screen.findByRole('heading', { name: 'IT Support dashboard' })).toBeInTheDocument()
  const cards = screen.getByRole('list', { name: 'Needs attention now' })
  expect(within(cards).getByRole('link', { name: /Active tickets/ })).toHaveAttribute('href', '/queue/all?dept=d1')
  expect(within(cards).getByText('4 open · 2 pending')).toBeInTheDocument()
  expect(within(cards).getByRole('link', { name: /SLA overdue/ })).toHaveAttribute('href', '/queue/all?dept=d1&sla=breached')
  expect(within(cards).getByRole('link', { name: /Due soon/ })).toHaveAttribute('href', '/queue/all?dept=d1&sla=duesoon')
  expect(within(cards).getByRole('link', { name: /Unassigned/ })).toHaveAttribute('href', '/queue/all?dept=d1&assignee=none')
  expect(within(cards).getByRole('link', { name: /Awaiting first response/ })).toHaveAttribute('href', '/queue/all?dept=d1&sla=awaitingresponse')
  expect(within(cards).getByRole('link', { name: /Email delivery problems/ })).toHaveAttribute('href', '/queue/all?dept=d1&send=uncertain')
  expect(within(cards).getByText('0 failed · 1 uncertain')).toBeInTheDocument()
  // the cards carry the same numbers as the server sent: nothing is derived or invented
  expect(within(within(cards).getByRole('link', { name: /SLA overdue/ })).getByText('2')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Ann Agent' })).toHaveAttribute('href', '/queue/all?dept=d1&assignee=u1')
  expect(screen.getByRole('link', { name: 'Unassigned' })).toHaveAttribute('href', '/queue/all?dept=d1&assignee=none')
  expect(screen.getByRole('list', { name: 'Backlog age' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'IT-7' })).toHaveAttribute('href', '/tickets/IT-7')
  const health = screen.getByRole('list', { name: 'Service health' })
  expect(within(health).getByText('Desk')).toBeInTheDocument()
  expect(within(health).getByText('Some sends have an uncertain outcome', { exact: false })).toBeInTheDocument()
  expect(screen.queryByText('desk@helpdesk.test')).not.toBeInTheDocument()
})

test('the activity chart is drawn from the server per-day series and says so when there is nothing', async () => {
  mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  const chart = await screen.findByRole('img', { name: /Tickets received and resolved per day/ })
  expect(chart).toBeInTheDocument()
  const totals = screen.getByRole('list', { name: 'Totals for the period' })
  expect(within(totals).getByText('5')).toBeInTheDocument()
  expect(within(totals).getByText('3')).toBeInTheDocument()
  expect(screen.getByRole('table', { name: 'Tickets received and resolved per day' })).toBeInTheDocument() // the same numbers for assistive technology
})

test('an empty period is an honest empty state, never an empty chart', async () => {
  mockFetch(routes(DASH, { 'GET /api/departments/d1/reports/volume': () => ({ ...VOLUME, received: 0, resolved: 0, daily: [] }) }))
  renderApp('/dashboard/d1')
  expect(await screen.findByText('No tickets were received or resolved in this period.')).toBeInTheDocument()
  expect(screen.queryByRole('img', { name: /Tickets received and resolved per day/ })).not.toBeInTheDocument()
})

test('choosing another period asks the server for it', async () => {
  const calls = mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  await screen.findByRole('img', { name: /Tickets received and resolved per day/ })
  fireEvent.click(screen.getByRole('button', { name: '30 days' }))
  await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/departments/d1/reports/volume')).length).toBeGreaterThanOrEqual(2))
  expect(screen.getByRole('button', { name: '30 days' })).toHaveAttribute('aria-pressed', 'true')
})

test('tickets needing attention come from the queue itself: overdue first, with a link to the full queue', async () => {
  mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  const list = await screen.findByRole('list', { name: 'Tickets needing attention' })
  expect(within(list).getByRole('link', { name: 'IT-9' })).toHaveAttribute('href', '/tickets/IT-9')
  expect(within(list).getByText('VPN down')).toBeInTheDocument()
  expect(within(list).getByText(/Overdue/)).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Open queue →' })).toHaveAttribute('href', '/queue/all?dept=d1&sla=breached')
})

test('nothing overdue or due soon says so', async () => {
  mockFetch(routes({ ...DASH, sla: { overdue: 0, dueSoon: 0, dueSoonWindowMinutes: 240 } }))
  renderApp('/dashboard/d1')
  expect(await screen.findByText('No ticket is overdue or due soon.')).toBeInTheDocument()
})

test('the refresh button asks the server again for the dashboard', async () => {
  const calls = mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  await screen.findByRole('heading', { name: 'IT Support dashboard' })
  const before = calls.filter((c) => c.path === '/api/departments/d1/dashboard').length
  fireEvent.click(screen.getByRole('button', { name: /Refresh/ }))
  await waitFor(() => expect(calls.filter((c) => c.path === '/api/departments/d1/dashboard').length).toBeGreaterThan(before))
})

test('mailbox health is absent when the server did not send it', async () => {
  mockFetch(routes({ ...DASH, mailboxes: null }))
  renderApp('/dashboard/d1')
  await screen.findByRole('heading', { name: 'IT Support dashboard' })
  expect(screen.queryByText('Desk')).not.toBeInTheDocument()
  expect(screen.getByText('Mailbox health is shown to mailbox administrators.')).toBeInTheDocument()
})

test('a failed refresh keeps the last data on screen and says it is stale', async () => {
  let n = 0
  mockFetch(routes(DASH, { 'GET /api/departments/d1/dashboard': () => (n++ === 0 ? DASH : new Response(null, { status: 500 })) }))
  renderApp('/dashboard/d1')
  await screen.findByRole('heading', { name: 'IT Support dashboard' })
  fireEvent.click(screen.getByRole('button', { name: /Refresh/ }))
  expect(await screen.findByText(/Could not refresh/)).toBeInTheDocument()
  expect(screen.getByRole('list', { name: 'Needs attention now' })).toBeInTheDocument()
})

test('with two departments the dashboard offers a picker and never an aggregate', async () => {
  const two = makeMe({
    memberships: [
      { ...membership(AGENT_PERMS), departmentId: 'd1', departmentName: 'IT Support', departmentKey: 'IT' },
      { ...membership(AGENT_PERMS), departmentId: 'd2', departmentName: 'HR', departmentKey: 'HR' },
    ],
  })
  mockFetch({ ...workspaceRoutes(two), ...csrfHandler })
  renderApp('/dashboard')
  expect(await screen.findByRole('link', { name: 'Open the HR dashboard' })).toHaveAttribute('href', '/dashboard/d2')
  expect(screen.getByRole('link', { name: 'Open the IT Support dashboard' })).toBeInTheDocument()
})
