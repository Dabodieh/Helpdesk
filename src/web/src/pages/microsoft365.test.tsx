import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, json, makeMe, mockFetch, problem, renderApp, type Call } from '../test-utils'

beforeEach(() => sessionStorage.clear())
afterEach(() => vi.unstubAllGlobals())

const PLATFORM = makeMe({ isPlatformAdmin: true })

const STATUS = {
  provider: 'Graph',
  simulated: false,
  graph: { configured: true, tenantId: 'tenant-1', clientId: 'client-1', credential: 'secret', baseUrl: 'https://graph.microsoft.com/v1.0' },
  signIn: { entraConfigured: true, tenantId: 'tenant-1', clientId: 'signin-1', secretPresent: true, devSignInEnabled: false, redirectPath: '/api/auth/signin-oidc' },
  notifications: { mode: 'webhook', baseUrl: 'https://help.example.org', publicHttps: true, reconcileMinutes: 5, subscriptionLifetimeDays: 3 },
  requiredPermissions: [
    { name: 'Application Mail.ReadWrite (Exchange RBAC for Applications)', purpose: 'Read mail.', scope: 'Helpdesk mailboxes only', required: true },
    { name: 'Tenant-wide Microsoft Graph Mail.* consent in Entra', purpose: 'Not required.', scope: 'Do not grant', required: false },
  ],
  mailboxes: [
    {
      id: 'mb1', departmentId: 'd1', departmentName: 'IT Support', address: 'desk@samh.example', displayName: 'IT Desk', isActive: true, isDefault: true,
      health: 'Attention', reasons: ['outbound_uncertain'], subscriptionState: 'Active', subscriptionExpiresAt: '2026-10-12T10:00:00Z', lastSyncAt: '2026-10-09T08:00:00Z', lastSyncFailed: false, outboundFailed: 0, outboundUncertain: 2,
    },
  ],
  issues: ['mailbox_attention'],
  provisioning: { available: false, reason: 'not_configured' },
  approvedDomains: [],
}

const routes = (extra: Record<string, (c: Call) => unknown> = {}) => ({
  'GET /api/me': () => PLATFORM,
  'GET /api/departments': () => [DEPT],
  'GET /api/admin/microsoft-365': () => STATUS,
  ...csrfHandler,
  ...extra,
})

test('the page shows identifiers and mailbox health, never a secret, and says mailbox creation is manual', async () => {
  mockFetch(routes())
  renderApp('/admin/microsoft-365')
  expect(await screen.findByRole('heading', { name: 'Microsoft 365', level: 1 })).toBeInTheDocument()
  expect(await screen.findByText('desk@samh.example')).toBeInTheDocument()
  expect(screen.getByText('IT Support')).toBeInTheDocument()
  expect(screen.getByText('At least one mailbox needs attention. See the table below.')).toBeInTheDocument()
  expect(screen.getByText('Manual')).toBeInTheDocument()
  expect(screen.getByText('signin-1')).toBeInTheDocument()
  expect(document.body.textContent).not.toMatch(/s3cr3t|clientSecret/i)

  fireEvent.click(screen.getByRole('button', { name: 'Details' }))
  expect(screen.getByText(/2 uncertain/)).toBeInTheDocument()
  expect(screen.getByText(/not retried automatically/)).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Manage in IT Support' })).toHaveAttribute('href', '/admin/departments/d1?section=mailboxes')
})

test('only a platform administrator is offered the page; the server refuses anyone else', async () => {
  mockFetch({ ...routes(), 'GET /api/me': () => makeMe(), 'GET /api/admin/microsoft-365': () => problem(403, 'Forbidden'), 'GET /health/ready': () => ({ status: 'Healthy' }) })
  renderApp('/admin/status')
  expect(await screen.findByRole('link', { name: 'System status' })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Microsoft 365' })).not.toBeInTheDocument()
})

test('connecting an existing mailbox: connection check, department, access check, then register (the only write)', async () => {
  const calls = mockFetch(
    routes({
      'POST /api/admin/microsoft-365/check-connection': () => ({ ok: true, code: 'ok', checkedAt: '2026-10-09T09:00:00Z' }),
      'POST /api/admin/microsoft-365/check-mailbox': () => ({ address: 'new@samh.example', valid: true, problem: null, registeredDepartmentId: null, registeredDepartmentName: null, access: 'ok' }),
      'POST /api/departments/d1/mailboxes': (c) =>
        json({ id: 'mb2', departmentId: 'd1', address: (c.body as { address: string }).address, displayName: 'New desk', provider: 'graph', isActive: true, isDefault: false, version: 'v1', subscription: { state: 'Active', expiresAt: null }, lastSyncAt: null, lastSyncError: null, attentionCount: 0 }, 201),
    }),
  )
  renderApp('/admin/microsoft-365')
  await screen.findByRole('heading', { name: 'Set up a mailbox' })
  const next = () => screen.getByRole('button', { name: 'Next' })
  fireEvent.click(next()) // tenant
  fireEvent.click(next()) // sign-in
  fireEvent.click(next()) // mail access

  expect(next()).toBeDisabled() // permissions: needs a successful connection check first
  fireEvent.click(screen.getByRole('button', { name: 'Check connection' }))
  expect(await screen.findByText('The application signed in successfully.')).toBeInTheDocument()
  fireEvent.click(next())

  expect(next()).toBeDisabled() // department
  fireEvent.change(await screen.findByLabelText('Destination department'), { target: { value: 'd1' } })
  fireEvent.click(next())

  fireEvent.change(screen.getByLabelText('Mailbox address'), { target: { value: 'new@samh.example' } })
  fireEvent.click(next())

  expect(next()).toBeDisabled() // test access: needs a reachable mailbox
  fireEvent.click(screen.getByRole('button', { name: 'Check mailbox' }))
  expect(await screen.findByText('The application can reach this mailbox.')).toBeInTheDocument()
  fireEvent.click(next())

  fireEvent.change(screen.getByLabelText('Display name in Helpdesk'), { target: { value: 'New desk' } })
  fireEvent.click(next())

  expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/mailboxes'))).toBe(false) // nothing is registered until the last step
  fireEvent.click(screen.getByRole('button', { name: 'Connect mailbox' }))
  expect(await screen.findByText(/new@samh.example is connected to IT Support/)).toBeInTheDocument()
  const post = calls.filter((c) => c.method === 'POST' && c.path === '/api/departments/d1/mailboxes')
  expect(post).toHaveLength(1)
  expect(post[0]!.body).toEqual({ address: 'new@samh.example', displayName: 'New desk' })
})

test('a mailbox that is already connected, or outside the application’s scope, blocks the wizard', async () => {
  let reply: Record<string, unknown> = { address: 'a@samh.example', valid: true, problem: 'already_registered', registeredDepartmentId: 'd9', registeredDepartmentName: 'Finance', access: 'skipped' }
  mockFetch(
    routes({
      'POST /api/admin/microsoft-365/check-connection': () => ({ ok: true, code: 'ok', checkedAt: 'x' }),
      'POST /api/admin/microsoft-365/check-mailbox': () => reply,
    }),
  )
  sessionStorage.setItem('helpdesk.m365.wizard', JSON.stringify({ step: 6, departmentId: 'd1', mode: 'existing', address: 'a@samh.example' }))
  renderApp('/admin/microsoft-365')
  fireEvent.click(await screen.findByRole('button', { name: 'Check mailbox' }))
  expect(await screen.findByText(/already connected to Finance/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()

  reply = { address: 'a@samh.example', valid: true, problem: null, registeredDepartmentId: null, registeredDepartmentName: null, access: 'denied' }
  fireEvent.click(screen.getByRole('button', { name: 'Check mailbox' }))
  await waitFor(() => expect(screen.getByText(/outside the application’s Exchange scope/)).toBeInTheDocument())
  expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
})

test('a new mailbox is a script for a tenant administrator: Helpdesk says it cannot create it and registers nothing', async () => {
  const calls = mockFetch(
    routes({
      'POST /api/admin/microsoft-365/new-mailbox-plan': () => ({
        valid: true, problems: {}, address: 'desk@samh.example', departmentName: 'IT Support', provisioning: { available: false, reason: 'not_configured' },
        script: ['Connect-ExchangeOnline', 'New-Mailbox -Shared -Name "Desk" -DisplayName "Desk" -Alias "desk" -PrimarySmtpAddress "desk@samh.example"'],
      }),
    }),
  )
  sessionStorage.setItem('helpdesk.m365.wizard', JSON.stringify({ step: 5, departmentId: 'd1', mode: 'new', displayName: 'Desk', alias: 'desk', domain: 'samh.example' }))
  renderApp('/admin/microsoft-365')
  expect(await screen.findByText(/Helpdesk cannot create Microsoft 365 mailboxes/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Prepare script' }))
  expect(await screen.findByLabelText('Exchange Online PowerShell')).toHaveTextContent('New-Mailbox -Shared')
  expect(screen.getByRole('button', { name: 'The mailbox exists: check access' })).toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/mailboxes'))).toBe(false)
})
