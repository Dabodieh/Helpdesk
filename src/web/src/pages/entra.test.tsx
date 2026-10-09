import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, json, makeMe, mockFetch, problem, renderApp, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const PLATFORM = makeMe({ isPlatformAdmin: true })
const STATUS_NONE = { source: 'none', configured: false, tenantId: null, clientId: null, secretExpiresAt: null, secretExpiresSoon: false, secretUnreadable: false, applicationObjectId: null, redirectOrigins: [], devSignInEnabled: true }
const M365 = {
  provider: 'Fake', simulated: true,
  graph: { configured: false, tenantId: null, clientId: null, credential: 'none', baseUrl: 'https://graph.microsoft.com/v1.0' },
  signIn: { entraConfigured: false, tenantId: null, clientId: null, secretPresent: false, devSignInEnabled: true, redirectPath: '/api/auth/signin-oidc' },
  notifications: { mode: 'simulated', baseUrl: null, publicHttps: false, reconcileMinutes: 5, subscriptionLifetimeDays: 3 },
  requiredPermissions: [], mailboxes: [], issues: [], provisioning: { available: false, reason: 'not_configured' }, approvedDomains: [], systemMailbox: null,
}

const routes = (entra: unknown, extra: Record<string, (c: Call) => unknown> = {}) => ({
  'GET /api/me': () => PLATFORM,
  'GET /api/departments': () => [DEPT],
  'GET /api/admin/microsoft-365': () => M365,
  'GET /api/admin/entra': () => entra,
  ...csrfHandler,
  ...extra,
})

test('connecting Entra: a code is shown, the page waits for Microsoft, then creates the application for the chosen addresses', async () => {
  const calls = mockFetch(
    routes(STATUS_NONE, {
      'POST /api/admin/entra/setup/start': () => ({ sessionId: 's1', userCode: 'ABCD-1234', verificationUri: 'https://microsoft.com/devicelogin', expiresInSeconds: 900, intervalSeconds: 0 }),
      'POST /api/admin/entra/setup/poll': () => ({ state: 'signed_in', intervalSeconds: 0, adminName: 'Alex Admin', adminAccount: 'alex@example.org', tenantId: 'tenant-1' }),
      'POST /api/admin/entra/setup/create': () => json({ tenantId: 'tenant-1', clientId: 'client-1', secretExpiresAt: '2027-10-09T10:00:00Z', redirectUris: ['http://localhost:3000/api/auth/signin-oidc'], signedInAs: 'alex@example.org', previousApplicationObjectId: null }),
    }),
  )
  renderApp('/admin/microsoft-365')
  expect(await screen.findByText('Not connected')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Connect Microsoft Entra ID' }))
  expect(calls.some((c) => c.path === '/api/admin/entra/setup/start')).toBe(false) // explained first, nothing contacted until Start
  fireEvent.click(screen.getByRole('button', { name: 'Start' }))

  expect(await screen.findByLabelText('Sign-in code')).toHaveTextContent('ABCD-1234')
  expect(await screen.findByText(/Alex Admin/)).toBeInTheDocument() // polled until Microsoft reported the sign-in

  fireEvent.change(screen.getByLabelText('Add another address'), { target: { value: 'https://helpdesk.example.org/' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add' }))
  fireEvent.click(screen.getByRole('button', { name: 'Create sign-in application' }))

  expect(await screen.findByText(/Microsoft sign-in is now on/)).toBeInTheDocument()
  expect(screen.getByText('client-1')).toBeInTheDocument()
  const create = calls.find((c) => c.path === '/api/admin/entra/setup/create')!
  expect(create.body).toEqual({ sessionId: 's1', redirectOrigins: [window.location.origin, 'https://helpdesk.example.org'] })
  expect(document.body.textContent).not.toMatch(/secret.*[A-Za-z0-9~._-]{30,}/i) // the secret is never in the page
})

test('a declined Microsoft sign-in is reported and a new code can be requested', async () => {
  mockFetch(
    routes(STATUS_NONE, {
      'POST /api/admin/entra/setup/start': () => ({ sessionId: 's1', userCode: 'WXYZ-9999', verificationUri: 'https://microsoft.com/devicelogin', expiresInSeconds: 900, intervalSeconds: 0 }),
      'POST /api/admin/entra/setup/poll': () => ({ state: 'declined', intervalSeconds: 0, adminName: null, adminAccount: null, tenantId: null }),
    }),
  )
  renderApp('/admin/microsoft-365')
  fireEvent.click(await screen.findByRole('button', { name: 'Connect Microsoft Entra ID' }))
  fireEvent.click(screen.getByRole('button', { name: 'Start' }))
  expect(await screen.findByText('The sign-in was declined in Microsoft.')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Get a new code' })).toBeInTheDocument()
})

test('settings from the server take priority and cannot be replaced here; stored settings can be reconnected or disconnected', async () => {
  mockFetch(routes({ ...STATUS_NONE, source: 'server', configured: true, tenantId: 't', clientId: 'c' }))
  renderApp('/admin/microsoft-365')
  expect(await screen.findByText('Connected (server settings)')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Connect Microsoft Entra ID|Reconnect/ })).not.toBeInTheDocument()

  vi.unstubAllGlobals()
  const calls = mockFetch(
    routes({ ...STATUS_NONE, source: 'database', configured: true, tenantId: 'tenant-1', clientId: 'client-1', secretExpiresAt: '2027-10-09T10:00:00Z', redirectOrigins: ['https://helpdesk.example.org'] }, { 'POST /api/admin/entra/disconnect': () => new Response(null, { status: 204 }) }),
  )
  renderApp('/admin/microsoft-365')
  expect(await screen.findByRole('button', { name: /Reconnect/ })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Disconnect' }))
  await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/entra/disconnect')).toBe(true))
})

test('the sign-in page says setup is needed only when the server says Microsoft sign-in is not connected', async () => {
  mockFetch({ 'GET /api/me': () => problem(401, 'Unauthorized'), 'GET /api/auth/providers': () => ({ entra: false }), ...csrfHandler })
  renderApp('/')
  expect(await screen.findByText(/Microsoft sign-in has not been set up yet/)).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Sign in' })).not.toBeInTheDocument()

  vi.unstubAllGlobals()
  mockFetch({ 'GET /api/me': () => problem(401, 'Unauthorized'), 'GET /api/auth/providers': () => ({ entra: true }), ...csrfHandler })
  renderApp('/')
  expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
})
