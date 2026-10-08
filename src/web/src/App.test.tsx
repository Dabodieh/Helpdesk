import { fireEvent, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { DEPT, makeMe, mockFetch, problem, renderApp } from './test-utils'

afterEach(() => vi.unstubAllGlobals())

test('unauthenticated users see the sign-in screen linking to the login endpoint', async () => {
  mockFetch({ 'GET /api/me': () => problem(401, 'Unauthorized') })
  renderApp('/queue/my/IT-1')
  const link = await screen.findByRole('link', { name: 'Sign in' })
  expect(link).toHaveAttribute('href', '/api/auth/login?returnUrl=%2Fqueue%2Fmy%2FIT-1')
  expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
})

test('signed-in users get the shell and the status page shows API health', async () => {
  mockFetch({
    'GET /api/me': () => makeMe(),
    'GET /api/departments': () => [DEPT],
    'GET /health/ready': [() => new Response('Healthy', { status: 200 }), () => new Response('Unhealthy', { status: 503 })],
  })
  renderApp('/admin/status')
  expect(await screen.findByText('API: Healthy')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }))
  expect(await screen.findByText('API: Unhealthy')).toBeInTheDocument()
  expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Account menu' }))
  expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument()
})

test('a 401 from any API call returns the user to sign-in', async () => {
  mockFetch({
    'GET /api/me': [() => makeMe(), () => problem(401, 'Unauthorized')],
    'GET /api/departments': () => problem(401, 'Unauthorized'),
  })
  renderApp('/admin')
  expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
})

test('sign out posts with the CSRF token and shows sign-in', async () => {
  const calls = mockFetch({
    'GET /api/me': [() => makeMe(), () => problem(401, 'Unauthorized')],
    'GET /api/departments': () => [],
    'GET /api/csrf': () => ({ headerName: 'X-CSRF-TOKEN', token: 'tok' }),
    'POST /api/auth/logout': () => new Response(null, { status: 204 }),
  })
  renderApp('/admin')
  fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
  const button = await screen.findByRole('button', { name: 'Sign out' })
  fireEvent.click(button)
  expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
  expect(calls.find((c) => c.path === '/api/auth/logout')?.headers['X-CSRF-TOKEN']).toBe('tok')
})
