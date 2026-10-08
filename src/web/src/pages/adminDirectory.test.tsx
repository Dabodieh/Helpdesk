import { screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, makeMe, mockFetch, renderApp } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const member = (permissions: string[], role = 'DepartmentAdmin') =>
  makeMe({ memberships: [{ departmentId: 'd1', departmentKey: 'IT', departmentName: 'IT Support', role, permissions }] })
const MAILBOX_ADMIN = member(['department.read', 'department.mailboxes.manage'])
const routes = (me = MAILBOX_ADMIN) => ({
  'GET /api/me': () => me,
  'GET /api/departments': () => [DEPT],
  'GET /api/departments/d1': () => DEPT,
  'GET /api/departments/d1/members': () => [],
  'GET /api/departments/d1/mailboxes': () => [{ id: 'mb1', address: 'desk@helpdesk.test', name: 'Desk', isDefault: true, isActive: true }],
  'GET /api/departments/d1/auto-reply': () => ({ departmentId: 'd1', configured: true, enabled: true, forEmail: true, forPortal: false, subject: 's', body: 'b', placeholders: [], version: 'v' }),
  ...csrfHandler,
})

test('/admin opens the Departments directory, with shortcuts only to settings the user may manage', async () => {
  mockFetch(routes())
  renderApp('/admin')
  expect(await screen.findByRole('heading', { name: 'Departments', level: 1 })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Settings hub' })).not.toBeInTheDocument()
  expect(await screen.findByRole('link', { name: 'System status' })).toBeInTheDocument()
  expect(screen.getByRole('list', { name: 'Department summary' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Mailboxes' })).toHaveAttribute('href', '/admin/departments/d1?section=mailboxes')
  expect(screen.queryByRole('link', { name: 'SLA policies' })).not.toBeInTheDocument()
})

test('the control centre lists only permitted settings and links straight to their section', async () => {
  mockFetch(routes())
  renderApp('/admin/departments/d1')
  expect(await screen.findByRole('link', { name: 'Open Acknowledgements' })).toHaveAttribute('href', '/admin/departments/d1?section=acknowledgements')
  expect(screen.getByRole('link', { name: 'Open Mailboxes' })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Open SLA policies' })).not.toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Open Operational dashboard' })).not.toBeInTheDocument()
})

test('the operational dashboard card needs ticket access, never the admin flag alone', async () => {
  mockFetch(routes(member(['department.read', 'department.mailboxes.manage', 'tickets.read'], 'Agent')))
  renderApp('/admin/departments/d1')
  expect((await screen.findByRole('link', { name: 'Open Operational dashboard' })).getAttribute('href')).toBe('/dashboard/d1')
})

test('a status that cannot load is shown as unavailable with Retry, and the setting stays reachable', async () => {
  mockFetch({ ...routes(), 'GET /api/departments/d1/mailboxes': () => new Response('{}', { status: 500, headers: { 'Content-Type': 'application/problem+json' } }) })
  renderApp('/admin/departments/d1')
  const retry = await screen.findByRole('button', { name: 'Retry Mailboxes status' })
  expect(screen.getByRole('link', { name: 'Open Mailboxes' })).toBeInTheDocument()
  await waitFor(() => expect(retry).toBeEnabled())
})
