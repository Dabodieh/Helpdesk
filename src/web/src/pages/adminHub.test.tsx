import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, makeMe, mockFetch, renderApp } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const member = (permissions: string[], role = 'DepartmentAdmin') =>
  makeMe({ memberships: [{ departmentId: 'd1', departmentKey: 'IT', departmentName: 'IT Support', role, permissions }] })
const MAILBOX_ADMIN = member(['department.read', 'department.mailboxes.manage'])
const hubRoutes = (me = MAILBOX_ADMIN) => ({
  'GET /api/me': () => me,
  'GET /api/departments': () => [DEPT, { ...DEPT, id: 'd2', key: 'HR', name: 'People Team' }],
  'GET /api/departments/d1': () => DEPT,
  'GET /api/departments/d1/mailboxes': () => [{ id: 'mb1', address: 'desk@helpdesk.test', name: 'Desk', isDefault: true, isActive: true }],
  'GET /api/departments/d1/auto-reply': () => ({ departmentId: 'd1', configured: true, enabled: true, forEmail: true, forPortal: false, subject: 's', body: 'b', placeholders: [], version: 'v' }),
  ...csrfHandler,
})

test('hub lists only settings the user may manage, for departments they can read', async () => {
  mockFetch(hubRoutes())
  renderApp('/admin')
  expect(await screen.findByRole('link', { name: 'Open Mailboxes' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Open Acknowledgements' })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Open SLA policies' })).not.toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Open Operational dashboard' })).not.toBeInTheDocument()
  // the department selector offers only departments the user holds department.read in
  expect(screen.getByRole('option', { name: 'IT Support' })).toBeInTheDocument()
  expect(screen.queryByRole('option', { name: 'People Team' })).not.toBeInTheDocument()
})

test('hub search filters the local catalogue and cards link straight to their section', async () => {
  mockFetch(hubRoutes())
  renderApp('/admin')
  await screen.findByRole('link', { name: 'Open Mailboxes' })
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search administration settings' }), { target: { value: 'confirmation' } })
  expect(await screen.findByRole('link', { name: 'Open Acknowledgements' })).toHaveAttribute('href', '/admin/departments/d1?section=acknowledgements')
  expect(screen.queryByRole('link', { name: 'Open Mailboxes' })).not.toBeInTheDocument()
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search administration settings' }), { target: { value: 'zzzz' } })
  expect(await screen.findByText('No matching settings')).toBeInTheDocument()
})

test('the operational dashboard card needs ticket access, never the admin flag alone', async () => {
  mockFetch(hubRoutes(member(['department.read', 'department.mailboxes.manage', 'tickets.read'], 'Agent')))
  renderApp('/admin')
  expect((await screen.findByRole('link', { name: 'Open Operational dashboard' })).getAttribute('href')).toBe('/dashboard/d1')
})

test('a status that cannot load is shown as unavailable with Retry, and the setting stays reachable', async () => {
  const routes = { ...hubRoutes(), 'GET /api/departments/d1/mailboxes': () => new Response('{}', { status: 500, headers: { 'Content-Type': 'application/problem+json' } }) }
  mockFetch(routes)
  renderApp('/admin')
  const retry = await screen.findByRole('button', { name: 'Retry Mailboxes status' })
  expect(screen.getByRole('link', { name: 'Open Mailboxes' })).toBeInTheDocument()
  await waitFor(() => expect(retry).toBeEnabled())
})
