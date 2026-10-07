import { screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { AGENT_PERMS, csrfHandler, makeMe, membership, mockFetch, renderApp, workspaceRoutes } from '../test-utils'

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
  oldest: [{ reference: 'IT-7', subject: 'Printer jam', status: 'Open', priority: 'High', createdAt: '2026-01-01T09:00:00Z', team: 'Network', assignee: null, slaOverdue: true }],
  teams: [
    { teamId: 'tm1', name: 'Network', total: 4, unassigned: 2, overdue: 1 },
    { teamId: null, name: 'No team', total: 2, unassigned: 1, overdue: 1 },
  ],
  sends: { failedTickets: 0, uncertainTickets: 1 },
  mailboxes: [{ mailboxId: 'mb1', address: 'desk@helpdesk.test', displayName: 'Desk', status: 'Attention', reasons: ['outbound_uncertain'], outboundFailed: 0, outboundUncertain: 1 }],
}

const routes = (dash: unknown) => ({ ...workspaceRoutes(), ...csrfHandler, 'GET /api/departments/d1/dashboard': () => dash })

test('the dashboard shows current figures for one department and every figure links into the existing queue', async () => {
  mockFetch(routes(DASH))
  renderApp('/dashboard/d1')
  expect(await screen.findByRole('heading', { name: 'IT Support dashboard' })).toBeInTheDocument()
  const attention = screen.getByRole('list', { name: 'Needs attention now' })
  expect(within(attention).getByRole('link', { name: /SLA overdue/ })).toHaveAttribute('href', '/queue/all?dept=d1&sla=breached')
  expect(within(attention).getByRole('link', { name: /Due soon/ })).toHaveAttribute('href', '/queue/all?dept=d1&sla=duesoon')
  expect(within(attention).getByRole('link', { name: /Unassigned/ })).toHaveAttribute('href', '/queue/all?dept=d1&assignee=none')
  expect(within(screen.getByRole('list', { name: 'Backlog' })).getByRole('link', { name: /Pending/ })).toHaveAttribute('href', '/queue/all?dept=d1&status=Pending')
  expect(screen.getByRole('link', { name: '4' })).toHaveAttribute('href', '/queue/all?dept=d1&team=tm1')
  expect(screen.getByRole('link', { name: 'IT-7' })).toHaveAttribute('href', '/tickets/IT-7')
  expect(screen.getByText('Mailboxes')).toBeInTheDocument()
  expect(screen.getByText('Some sends have an uncertain outcome')).toBeInTheDocument()
})

test('mailbox health is absent when the server did not send it, and a user with several departments chooses one explicitly', async () => {
  mockFetch(routes({ ...DASH, mailboxes: null }))
  renderApp('/dashboard/d1')
  await screen.findByRole('heading', { name: 'IT Support dashboard' })
  expect(screen.queryByText('Mailboxes')).not.toBeInTheDocument()
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
