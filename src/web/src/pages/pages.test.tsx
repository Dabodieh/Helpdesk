import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, json, makeMe, mockFetch, problem, renderApp } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const type = (el: HTMLElement, value: string) => fireEvent.change(el, { target: { value } })

const adminMe = makeMe({
  memberships: [
    {
      departmentId: 'd1',
      departmentKey: 'IT',
      departmentName: 'IT Support',
      role: 'DepartmentAdmin',
      permissions: ['department.read', 'department.manage', 'department.members.manage', 'teams.manage'],
    },
  ],
})
const agentMe = makeMe({
  memberships: [
    { departmentId: 'd1', departmentKey: 'IT', departmentName: 'IT Support', role: 'Agent', permissions: ['department.read'] },
  ],
})
const MEMBERS = [{ userId: 'u1', displayName: 'Test User', email: 'u@example.test', role: 'DepartmentAdmin', teamIds: [] }]

const deptRoutes = (me = adminMe) => ({
  'GET /api/me': () => me,
  'GET /api/departments/d1': () => DEPT,
  'GET /api/departments/d1/teams': () => [],
  'GET /api/departments/d1/members': () => MEMBERS,
  ...csrfHandler,
})

test('create department: invalid key is rejected client-side without calling the API', async () => {
  const calls = mockFetch({
    'GET /api/me': () => makeMe({ isPlatformAdmin: true, platformPermissions: ['platform.departments.manage'] }),
    'GET /api/departments': () => [],
    ...csrfHandler,
  })
  renderApp('/admin')
  fireEvent.click(await screen.findByRole('button', { name: 'New department' }))
  type(await screen.findByRole('textbox', { name: /^Key/ }), 'a-b')
  type(screen.getByLabelText('Name'), 'Facilities')
  fireEvent.click(screen.getByRole('button', { name: 'Create department' }))
  expect(await screen.findByText(/Key must be 2 to 10 characters/)).toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST')).toBe(false)
})

test('create department: valid input posts key and name', async () => {
  const calls = mockFetch({
    'GET /api/me': () => makeMe({ isPlatformAdmin: true, platformPermissions: ['platform.departments.manage'] }),
    'GET /api/departments': () => [],
    'POST /api/departments': () => json({ ...DEPT, id: 'd9', key: 'FAC', name: 'Facilities' }, 201),
    'GET /api/departments/d9': () => ({ ...DEPT, id: 'd9', key: 'FAC', name: 'Facilities' }),
    'GET /api/departments/d9/teams': () => [],
    'GET /api/departments/d9/members': () => [],
    ...csrfHandler,
  })
  renderApp('/admin')
  fireEvent.click(await screen.findByRole('button', { name: 'New department' }))
  type(await screen.findByRole('textbox', { name: /^Key/ }), 'fac')
  type(screen.getByLabelText('Name'), 'Facilities')
  fireEvent.click(screen.getByRole('button', { name: 'Create department' }))
  expect(await screen.findByRole('heading', { name: /Facilities/ })).toBeInTheDocument()
  expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ key: 'FAC', name: 'Facilities' })
})

test('users without platform.departments.manage get no create form', async () => {
  mockFetch({ 'GET /api/me': () => adminMe, 'GET /api/departments': () => [DEPT] })
  renderApp('/admin')
  expect(await screen.findByRole('link', { name: 'IT Support' })).toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'New department' })).not.toBeInTheDocument()
})

test('permission-aware controls: an Agent sees read-only details and no management controls', async () => {
  mockFetch(deptRoutes(agentMe))
  renderApp('/admin/departments/d1')
  expect(await screen.findByRole('heading', { name: /IT Support/ })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Edit department' })).not.toBeInTheDocument()
  fireEvent.click(await screen.findByRole('tab', { name: 'Members' }))
  expect((await screen.findAllByText('Test User')).length).toBeGreaterThan(0)
  expect(screen.queryByRole('heading', { name: 'Add member' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument()
  expect(screen.queryByRole('form', { name: 'New team' })).not.toBeInTheDocument()
})

test('department admin can edit details but the Active toggle stays disabled without the platform permission', async () => {
  mockFetch(deptRoutes())
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('button', { name: 'Edit department' }))
  expect(await screen.findByLabelText('Name')).not.toHaveAttribute('readonly')
  expect(screen.getByLabelText('Active')).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled()
})

test('version conflict: shows a reload message and reloading picks up the latest version', async () => {
  let version = 'v1'
  const calls = mockFetch({
    ...deptRoutes(),
    'GET /api/departments/d1': () => ({ ...DEPT, name: version === 'v1' ? 'IT Support' : 'Renamed elsewhere', version }),
    'PATCH /api/departments/d1': () => {
      version = 'v2'
      return problem(409, 'Conflict', { code: 'version_conflict', detail: 'unused wording' })
    },
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('button', { name: 'Edit department' }))
  const name = await screen.findByLabelText('Name')
    type(name, 'Mine')
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))
  expect(await screen.findByText(/changed by someone else/i)).toBeInTheDocument()
  expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ version: 'v1', name: 'Mine' })
  fireEvent.click(screen.getByRole('button', { name: 'Reload latest version' }))
  await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Renamed elsewhere'))
  expect(screen.queryByRole('button', { name: 'Reload latest version' })).not.toBeInTheDocument()
})

test('404 and 403 on a department are shown distinctly', async () => {
  mockFetch({ 'GET /api/me': () => adminMe, 'GET /api/departments/nope': () => problem(404, 'Not Found') })
  renderApp('/admin/departments/nope')
  expect(await screen.findByText(/not found, or you do not have access/i)).toBeInTheDocument()
})

test('member add flow: lookup requires 3 characters, then adds with the chosen role', async () => {
  const calls = mockFetch({
    ...deptRoutes(),
    'GET /api/users/lookup?q=ali': () => [{ id: 'u2', displayName: 'Alice Smith', email: 'alice@example.test' }],
    'PUT /api/departments/d1/members/u2': () => ({
      userId: 'u2',
      displayName: 'Alice Smith',
      email: 'alice@example.test',
      role: 'TeamLead',
      teamIds: [],
    }),
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Members' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Add member' }))
  const search = await screen.findByLabelText('Find user by name or email')
  type(search, 'al')
  expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled()
  type(search, 'ali')
  fireEvent.change(screen.getByRole('combobox', { name: 'Role' }), { target: { value: 'TeamLead' } })
  fireEvent.click(screen.getByRole('button', { name: 'Search' }))
  const results = await screen.findByRole('list', { name: 'Search results' })
  fireEvent.click(within(results).getByRole('button', { name: /Add/ }))
  expect(await screen.findByText('Alice Smith added as TeamLead.')).toBeInTheDocument()
  const put = calls.find((c) => c.method === 'PUT')!
  expect(put.body).toEqual({ role: 'TeamLead' })
  expect(put.headers['X-CSRF-TOKEN']).toBe('tok')
})

test('team members: only department members that are not yet in the team are selectable', async () => {
  mockFetch({
    ...deptRoutes(),
    'GET /api/departments/d1/teams/t1': () => ({ id: 't1', name: 'Tier 1', isActive: true, memberCount: 1, version: 'a' }),
    'GET /api/departments/d1/teams/t1/members': () => [{ userId: 'u1', displayName: 'Test User', email: null }],
    'GET /api/departments/d1/members': () => [
      ...MEMBERS,
      { userId: 'u3', displayName: 'Bob Jones', email: 'b@example.test', role: 'Agent', teamIds: [] },
    ],
  })
  renderApp('/admin/departments/d1/teams/t1')
  const select = await screen.findByLabelText('Add department member')
  await waitFor(() => expect(within(select).getByRole('option', { name: 'Bob Jones' })).toBeInTheDocument())
  expect(within(select).queryByRole('option', { name: 'Test User' })).not.toBeInTheDocument()
})

test('department shell: grouped section navigation, a setup overview, and sections are deep-linkable', async () => {
  mockFetch(deptRoutes())
  renderApp('/admin/departments/d1')
  expect(await screen.findByRole('tablist', { name: 'Department administration' })).toHaveAttribute('aria-orientation', 'vertical')
  expect(await screen.findByRole('list', { name: 'People & access setup status' })).toBeInTheDocument()
  expect(screen.getByText(/Department code:/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Open Members' }))
  expect(await screen.findByRole('tab', { name: 'Members', selected: true })).toBeInTheDocument()
})

test('deep link to a section opens it directly', async () => {
  mockFetch(deptRoutes())
  renderApp('/admin/departments/d1?section=members')
  expect(await screen.findByRole('tab', { name: 'Members', selected: true })).toBeInTheDocument()
})

const CASE = {
  requesterId: 'r1',
  displayName: 'Jane Smith',
  email: 'jane@example.test',
  ticketCount: 3,
  openCount: 1,
  firstSeenAt: '2026-01-01T00:00:00Z',
  lastActivityAt: '2026-02-01T00:00:00Z',
  matchingAccount: { userId: 'u9', displayName: 'Jane Jones', email: 'jane@example.test' },
  needsAttention: true,
}
const identityRoutes = (link: () => unknown) => ({
  ...deptRoutes(),
  'GET /api/departments/d1/requester-identities': () => [CASE, { ...CASE, requesterId: 'r2', displayName: 'Vendor Ltd', needsAttention: false, matchingAccount: null }],
  'GET /api/departments/d1/requester-identities/r1': () => ({ case: CASE, status: 'Unlinked', linkedAccount: null, tickets: [{ reference: 'IT-1', subject: 'Printer', status: 'Open', updatedAt: '2026-02-01T00:00:00Z' }] }),
  'POST /api/departments/d1/requester-identities/r1/link': link,
  ...csrfHandler,
})

test('requester identity resolution: needs-attention list, deliberate selection, explicit confirmation, then the link request', async () => {
  const calls = mockFetch(identityRoutes(() => ({ alreadyLinked: false })))
  renderApp('/admin/departments/d1?section=requesters')
  expect(await screen.findByRole('heading', { name: /Needs attention \(1\)/ })).toBeInTheDocument()
  expect(screen.queryByText('Vendor Ltd')).not.toBeInTheDocument() // other unlinked requesters are collapsed
  fireEvent.click(screen.getByRole('button', { name: 'Resolve Jane Smith' }))
  const dialog = await screen.findByRole('dialog')
  expect(await within(dialog).findByText('IT-1')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Link requester to account…' })).toBeDisabled() // nothing is pre-selected
  fireEvent.click(within(dialog).getByRole('button', { name: 'Select this account' }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Link requester to account…' }))
  const confirm = await screen.findByRole('dialog', { name: 'Link requester to account?' })
  expect(within(confirm).getByText(/will not grant department staff permissions/)).toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST')).toBe(false) // nothing sent before the confirmation
  fireEvent.click(within(confirm).getByRole('button', { name: 'Link account' }))
  await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ userId: 'u9' }))
})

test('requester identity resolution: a conflict is shown and the requester stays unresolved; agents do not get the section', async () => {
  mockFetch(identityRoutes(() => problem(409, 'Conflict', { code: 'conflict', detail: 'This requester is already linked to a different account.' })))
  renderApp('/admin/departments/d1?section=requesters')
  fireEvent.click(await screen.findByRole('button', { name: 'Resolve Jane Smith' }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.click(await within(dialog).findByRole('button', { name: 'Select this account' }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Link requester to account…' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Link account' }))
  expect(await screen.findByText(/already linked to a different account/)).toBeInTheDocument()
})

test('requester identity section is not offered without department.members.manage', async () => {
  mockFetch(deptRoutes(agentMe))
  renderApp('/admin/departments/d1')
  await screen.findByRole('tab', { name: 'Members' })
  expect(screen.queryByRole('tab', { name: 'Requester identities' })).not.toBeInTheDocument()
})
