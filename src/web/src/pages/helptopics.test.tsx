import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, DEPT, json, makeMe, membership, mockFetch, problem, renderApp } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const FORM = { id: 'f1', name: 'Hardware issue', description: null, department: 'IT Support' }
const TOPIC = { id: 't1', name: 'Report an IT problem', description: 'Something is broken', department: 'IT Support', formId: 'f1' }
const portalMe = () => makeMe({ memberships: [] })

test('the portal catalogue shows common requests first and the form list below, and a topic opens its request form', async () => {
  mockFetch({
    'GET /api/me': () => portalMe(),
    'GET /api/portal/forms': () => [FORM],
    'GET /api/portal/help-topics': () => [TOPIC],
    'GET /api/portal/help-topics/t1': () => TOPIC,
    'GET /api/portal/forms/f1': () => ({ id: 'f1', name: 'Hardware issue', description: null, department: 'IT Support', fields: [] }),
    'GET /api/portal/knowledge/articles': () => ({ items: [], nextCursor: null }),
    ...csrfHandler,
  })
  renderApp('/portal/submit')
  expect(await screen.findByRole('heading', { name: 'Common requests' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Report an IT problem/ })).toHaveAttribute('href', '/portal/topics/t1')
  expect(screen.getByRole('heading', { name: 'All request types' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('link', { name: /Report an IT problem/ }))
  expect(await screen.findByRole('heading', { name: 'Hardware issue', level: 1 })).toBeInTheDocument() // the ordinary form workflow
})

test('a topic that is no longer offered is a plain not-found with a way back', async () => {
  mockFetch({ 'GET /api/me': () => portalMe(), 'GET /api/portal/help-topics/t9': () => problem(404, 'Not Found'), ...csrfHandler })
  renderApp('/portal/topics/t9')
  expect(await screen.findByRole('link', { name: 'Back to request types' })).toBeInTheDocument()
})

test('administration: create a topic by choosing an existing form, and reorder topics', async () => {
  const admin = makeMe({ memberships: [membership(['department.read', 'department.manage', 'department.forms.manage'], 'DepartmentAdmin')] })
  const row = (id: string, name: string, order: number) => ({ id, departmentId: 'd1', name, description: null, isActive: true, order, formId: 'f1', formName: 'Hardware issue', formAvailable: true, version: 'v1' })
  const calls = mockFetch({
    'GET /api/me': () => admin,
    [`GET /api/departments/${DEPT.id}`]: () => DEPT,
    [`GET /api/departments/${DEPT.id}/teams`]: () => [],
    [`GET /api/departments/${DEPT.id}/members`]: () => [],
    [`GET /api/departments/${DEPT.id}/ticket-types`]: () => [],
    [`GET /api/departments/${DEPT.id}/ticket-fields`]: () => [],
    [`GET /api/departments/${DEPT.id}/request-forms`]: () => [{ id: 'f1', departmentId: 'd1', name: 'Hardware issue', isActive: true, visibleToRequesters: true, order: 10, fields: [], version: 'v1', audience: 'AllEmployees', audienceUsers: [] }],
    [`GET /api/departments/${DEPT.id}/help-topics`]: () => [row('t1', 'First', 10), row('t2', 'Second', 20)],
    [`POST /api/departments/${DEPT.id}/help-topics`]: () => json(row('t3', 'Request software', 30), 201),
    [`PUT /api/departments/${DEPT.id}/help-topics/order`]: () => [row('t2', 'Second', 10), row('t1', 'First', 20)],
    ...csrfHandler,
  })
  renderApp(`/admin/departments/${DEPT.id}?section=ticketing`)
  fireEvent.click(await screen.findByRole('tab', { name: 'Help topics' }))
  expect(await screen.findAllByText('First')).not.toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Move First down' }))
  await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ ids: ['t2', 't1'] }))

  fireEvent.click(screen.getByRole('button', { name: 'New help topic' }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Topic name'), { target: { value: 'Request software' } })
  fireEvent.change(await within(dialog).findByLabelText('Opens request form'), { target: { value: 'f1' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save topic' }))
  await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ name: 'Request software', formId: 'f1', isActive: true }))
})
