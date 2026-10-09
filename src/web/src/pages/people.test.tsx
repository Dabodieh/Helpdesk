import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, json, makeMe, mockFetch, problem, renderApp, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const PLATFORM = makeMe({ isPlatformAdmin: true })
const INVITATION = {
  id: 'i1', displayName: 'Ana Smith', email: 'ana@samh.example', departmentId: 'd1', departmentName: 'IT Support', role: 'Agent', status: 'Pending',
  expiresAt: '2026-10-16T10:00:00Z', createdAt: '2026-10-09T10:00:00Z', emailState: 'Accepted', acceptedAt: null,
}

const routes = (extra: Record<string, (c: Call) => unknown> = {}) => ({
  'GET /api/me': () => PLATFORM,
  'GET /api/departments': () => [DEPT],
  'GET /api/admin/invitations': () => [INVITATION],
  ...csrfHandler,
  ...extra,
})

test('an administrator invites a person: the request carries only what was typed, and the one-time link is shown', async () => {
  const calls = mockFetch(
    routes({
      'POST /api/admin/invitations': () => json({ invitation: { ...INVITATION, id: 'i2', displayName: 'Ben Jones', email: 'ben@samh.example', emailState: 'NotConfigured' }, activationUrl: 'http://localhost:5173/activate/tok123' }, 201),
    }),
  )
  renderApp('/admin/people')
  expect(await screen.findByText('ana@samh.example')).toBeInTheDocument()
  expect(screen.getByText('Sent (accepted for delivery)')).toBeInTheDocument()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Ben Jones' } })
  fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'ben@samh.example' } })
  fireEvent.change(screen.getByLabelText('Department'), { target: { value: 'd1' } })
  fireEvent.change(screen.getByLabelText('Role'), { target: { value: 'Viewer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send invitation' }))

  expect(await screen.findByText(/Invitation created for ben@samh.example, but the email was not sent/)).toBeInTheDocument()
  expect(screen.getByText('http://localhost:5173/activate/tok123')).toBeInTheDocument()
  const post = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/invitations')!
  expect(post.body).toEqual({ displayName: 'Ben Jones', email: 'ben@samh.example', departmentId: 'd1', role: 'Viewer' })
})

test('an open invitation can be resent (a new link) or revoked; one that was used has no actions', async () => {
  const calls = mockFetch(
    routes({
      'GET /api/admin/invitations': () => [INVITATION, { ...INVITATION, id: 'i3', displayName: 'Done Person', email: 'done@samh.example', status: 'Accepted', acceptedAt: '2026-10-09T11:00:00Z' }],
      'POST /api/admin/invitations/i1/resend': () => json({ invitation: INVITATION, activationUrl: 'http://localhost:5173/activate/newtok' }),
      'POST /api/admin/invitations/i1/revoke': () => new Response(null, { status: 204 }),
    }),
  )
  renderApp('/admin/people')
  await screen.findByText('ana@samh.example')
  expect(screen.getAllByRole('button', { name: 'Resend' })).toHaveLength(1)

  fireEvent.click(screen.getByRole('button', { name: 'Resend' }))
  expect(await screen.findByText('http://localhost:5173/activate/newtok')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Revoke' }))
  await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/invitations/i1/revoke')).toBe(true))
})

test('the emailed link asks the signed-in person to activate, then reports where they were added', async () => {
  const calls = mockFetch({
    'GET /api/me': () => makeMe({ email: 'ana@samh.example' }),
    'POST /api/invitations/preview': () => ({ displayName: 'Ana Smith', departmentName: 'IT Support', role: 'Agent' }),
    'POST /api/invitations/accept': () => ({ departmentId: 'd1', departmentName: 'IT Support', role: 'Agent', alreadyMember: false }),
    ...csrfHandler,
  })
  renderApp('/activate/tok123')
  expect(await screen.findByText(/You have been invited to/)).toBeInTheDocument()
  expect(screen.getByText('Signed in as ana@samh.example.')).toBeInTheDocument()
  expect(calls.some((c) => c.path === '/api/invitations/accept')).toBe(false) // nothing happens until the button is pressed

  fireEvent.click(screen.getByRole('button', { name: 'Activate my account' }))
  expect(await screen.findByText('You have been added to IT Support as Agent.')).toBeInTheDocument()
  expect((calls.find((c) => c.path === '/api/invitations/accept')!.body as { token: string }).token).toBe('tok123')
  expect(screen.getByRole('link', { name: 'Open Helpdesk' })).toBeInTheDocument()
})

test('a link that is used, expired or replaced says it is not valid, and a different account is told why', async () => {
  mockFetch({ 'GET /api/me': () => makeMe(), 'POST /api/invitations/preview': () => problem(404, 'Not found'), ...csrfHandler })
  renderApp('/activate/old')
  expect(await screen.findByText(/This link is not valid/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Activate my account' })).not.toBeInTheDocument()

  vi.unstubAllGlobals()
  mockFetch({
    'GET /api/me': () => makeMe({ email: 'other@samh.example' }),
    'POST /api/invitations/preview': () => ({ displayName: 'Ana Smith', departmentName: 'IT Support', role: 'Agent' }),
    'POST /api/invitations/accept': () => problem(409, 'Conflict', { code: 'invalid_state', detail: 'This invitation was issued for a different email address. Sign in with the Microsoft 365 account it was sent to.' }),
    ...csrfHandler,
  })
  renderApp('/activate/tok123')
  fireEvent.click(await screen.findByRole('button', { name: 'Activate my account' }))
  expect(await screen.findByText(/issued for a different email address/)).toBeInTheDocument()
})
