import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, csrfHandler, DEPT, leadMe, makeDetail, makeMe, makeMessage, membership, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const PICK = { id: 'm1', name: 'Resolve password reset', description: 'Done and dusted', summary: ['Status → Resolved', 'Priority → Normal', 'Insert "Password Reset Complete" into the Public Reply (draft, not sent)'] }

const ticketRoutes = (me = leadMe()) => ({
  ...workspaceRoutes(me),
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
  'GET /api/tickets/t1/followers': () => ({ items: [], following: false }),
  'GET /api/tickets/t1/macros?q=': () => [PICK],
  'GET /api/tickets/t1/macros?q=pass': () => [PICK],
})

test('an agent searches a macro, sees what it will do, applies it, stays on the ticket and gets an UNSENT reply draft', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/macros/m1/run': () => ({
      ticket: makeDetail({ status: 'Resolved', version: 'v2' }),
      draft: { body: 'Your password has been reset.', cannedResponseId: 'c1', title: 'Password Reset Complete' },
      noteAdded: false,
    }),
  })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'More' }))
  fireEvent.click(screen.getByRole('button', { name: 'Run macro' }))
  const picker = await screen.findByRole('dialog', { name: 'Run macro' })
  fireEvent.change(within(picker).getByLabelText('Search macros'), { target: { value: 'pass' } })
  fireEvent.click(await within(picker).findByRole('button', { name: /Resolve password reset/ }))

  const preview = await screen.findByRole('dialog', { name: 'Resolve password reset' })
  expect(within(preview).getByText('Status → Resolved')).toBeInTheDocument()
  expect(within(preview).getByText(/into the Public Reply \(draft, not sent\)/)).toBeInTheDocument()
  expect(within(preview).getByText(/Nothing is emailed/)).toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST' && c.path.includes('/run'))).toBe(false) // nothing happens before Apply
  fireEvent.click(within(preview).getByRole('button', { name: 'Apply macro' }))

  // The run names the current ticket version; afterwards the dialog closes, the ticket is still open, and the draft sits in the reply box.
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(calls.find((c) => c.path.endsWith('/run'))!.body).toEqual({ version: 'v1' })
  const reply = await screen.findByRole('textbox', { name: /reply|message/i })
  expect((reply as HTMLTextAreaElement).value).toBe('Your password has been reset.')
  expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/messages'))).toBe(false) // never sent by the macro
})

test('a macro the server refuses shows the problem and leaves the ticket alone', async () => {
  mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/macros/m1/run': () =>
      new Response(JSON.stringify({ status: 409, title: 'Conflict', detail: 'The canned response of this macro is no longer available.', code: 'canned_unavailable' }), {
        status: 409,
        headers: { 'content-type': 'application/problem+json' },
      }),
  })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'More' }))
  fireEvent.click(screen.getByRole('button', { name: 'Run macro' }))
  fireEvent.click(await screen.findByRole('button', { name: /Resolve password reset/ }))
  fireEvent.click(await screen.findByRole('button', { name: 'Apply macro' }))
  expect(await screen.findByText(/no longer available/)).toBeInTheDocument()
  expect(screen.getByRole('dialog', { name: 'Resolve password reset' })).toBeInTheDocument()
})

test('admins build a macro with structured controls only and the server action errors appear next to the action', async () => {
  const adminMe = makeMe({ memberships: [membership(['department.read', 'department.manage', 'department.canned.manage'], 'DepartmentAdmin')] })
  const calls = mockFetch({
    'GET /api/me': () => adminMe,
    'GET /api/departments/d1': () => DEPT,
    'GET /api/departments/d1/teams': () => [{ id: 'tm1', name: 'Service Desk', isActive: true, version: 'v' }],
    'GET /api/departments/d1/members': () => [],
    'GET /api/departments/d1/macros': () => [{ id: 'm1', departmentId: 'd1', name: 'Existing', description: null, isActive: true, order: 10, actions: [{ type: 'setPriority', value: 'High' }], summary: ['Priority → High'], version: 'v1' }],
    'GET /api/departments/d1/ticket-types': () => [],
    'GET /api/departments/d1/ticket-fields': () => [],
    'GET /api/canned-responses?departmentId=d1': () => [],
    'POST /api/departments/d1/macros': () =>
      new Response(JSON.stringify({ status: 400, title: 'Validation failed', errors: { 'actions[0]': ['The team does not belong to this department.'] } }), {
        status: 400,
        headers: { 'content-type': 'application/problem+json' },
      }),
    'PATCH /api/departments/d1/macros/m1': () => ({ id: 'm1', departmentId: 'd1', name: 'Existing', description: null, isActive: false, order: 10, actions: [], summary: [], version: 'v2' }),
    ...csrfHandler,
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Macros' }))
  expect(await screen.findByText('Priority → High')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Deactivate' }))
  await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')!.body).toEqual({ version: 'v1', isActive: false }))

  fireEvent.click(screen.getByRole('button', { name: 'New macro' }))
  const dialog = await screen.findByRole('dialog', { name: 'New macro' })
  expect(within(dialog).queryByRole('textbox', { name: /json/i })).not.toBeInTheDocument()
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Route it' } })
  fireEvent.change(within(dialog).getByRole('combobox', { name: /Add action/ }), { target: { value: 'setTeam' } })
  await within(dialog).findByRole('option', { name: 'Service Desk' })
  fireEvent.change(within(dialog).getByLabelText('Team'), { target: { value: 'tm1' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save macro' }))
  expect(await within(dialog).findByText('The team does not belong to this department.')).toBeInTheDocument()
  expect(calls.find((c) => c.method === 'POST' && c.path.endsWith('/macros'))!.body).toMatchObject({ name: 'Route it', isActive: true, actions: [{ type: 'setTeam', value: 'tm1' }] })
})

test('without update access to the ticket there is no macro entry point', async () => {
  mockFetch({ ...ticketRoutes(agentMe()), 'GET /api/me': () => makeMe({ memberships: [membership(['department.read', 'tickets.read'], 'Viewer')] }) })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  await within(pane).findByRole('region', { name: 'Followers' })
  expect(within(pane).queryByRole('button', { name: 'More' })).not.toBeInTheDocument()
})
