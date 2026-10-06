import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, makeDetail, makeMe, makeMessage, membership, mockFetch, page, renderApp, workspaceRoutes, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const PROPERTIES = {
  type: { id: 'ty1', displayName: 'Hardware' },
  types: [
    { id: 'ty1', name: 'Hardware', isActive: true, isDefault: false },
    { id: 'ty2', name: 'Access', isActive: true, isDefault: true },
  ],
  version: 'v9',
  fields: [
    { fieldId: 'f1', key: 'location', label: 'Location', helpText: null, type: 'ShortText', required: false, options: [], value: 'HQ', display: 'HQ', applicable: true, editable: true, active: true, missingRequired: false },
    { fieldId: 'f2', key: 'tag', label: 'Asset tag', helpText: null, type: 'ShortText', required: true, options: [], value: null, display: null, applicable: true, editable: true, active: true, missingRequired: true },
    { fieldId: 'f3', key: 'device', label: 'Device', helpText: null, type: 'ShortText', required: false, options: [], value: 'old', display: 'Old kit', applicable: false, editable: false, active: false, missingRequired: false },
  ],
}

const routes = (calls?: Call[]) => ({
  ...workspaceRoutes(),
  ...csrfHandler,
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/IT-1/properties': () => PROPERTIES,
  'PATCH /api/tickets/IT-1/properties': (c: Call) => {
    calls?.push(c)
    return { ...PROPERTIES, version: 'v10' }
  },
})

test('ticket details list the type and fields, flag a missing required value and keep retired values readable', async () => {
  mockFetch(routes())
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  const details = await within(pane).findByText('Required, not set')
  expect(details).toBeInTheDocument()
  expect(within(pane).getByText('HQ')).toBeInTheDocument()
  expect(within(pane).getByText('Old kit')).toBeInTheDocument()
  expect(within(pane).getByText('(field retired)')).toBeInTheDocument()
  expect(within(pane).getByText('Needs details')).toBeInTheDocument()
})

test('editing details sends only what changed, with the ticket version, and offers editable fields only', async () => {
  const calls: Call[] = []
  mockFetch(routes(calls))
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'Edit details' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit ticket details' })
  expect(within(dialog).queryByLabelText(/Device/)).not.toBeInTheDocument() // retired and not applicable: not editable
  expect(within(dialog).getByRole('button', { name: 'Save details' })).toBeDisabled()
  fireEvent.change(within(dialog).getByLabelText('Asset tag'), { target: { value: 'T-9' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save details' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
  const body = calls.find((c) => c.method === 'PATCH')!.body as { version: string; fields: Record<string, unknown>; ticketTypeId?: string }
  expect(body).toEqual({ version: 'v9', fields: { tag: 'T-9' } })
})

test('the department page offers a Ticketing area with types, fields and forms for an administrator', async () => {
  const admin = makeMe({ memberships: [membership(['department.read', 'department.manage', 'department.forms.manage'], 'DepartmentAdmin')] })
  mockFetch({
    ...csrfHandler,
    'GET /api/me': () => admin,
    [`GET /api/departments/${DEPT.id}`]: () => DEPT,
    [`GET /api/departments/${DEPT.id}/teams`]: () => [],
    [`GET /api/departments/${DEPT.id}/members`]: () => [],
    [`GET /api/departments/${DEPT.id}/ticket-types`]: () => [],
    [`GET /api/departments/${DEPT.id}/ticket-fields`]: () => [],
    [`GET /api/departments/${DEPT.id}/request-forms`]: () => [],
  })
  renderApp(`/admin/departments/${DEPT.id}`)
  fireEvent.click(await screen.findByRole('tab', { name: 'Ticketing' }))
  expect(await screen.findByRole('tab', { name: 'Ticket types' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Ticket fields' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Request forms' })).toBeInTheDocument()
  expect(await screen.findByText(/No ticket types yet/)).toBeInTheDocument()
})
