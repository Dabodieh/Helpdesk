import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { agentMe, leadMe, makeDetail, makeMessage, makeMe, membership, mockFetch, page, renderApp, workspaceRoutes, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())
beforeEach(() => window.localStorage.clear())

const PROPS = {
  type: { id: 'ty1', displayName: 'Hardware' },
  types: [
    { id: 'ty1', name: 'Hardware', isActive: true, isDefault: false },
    { id: 'ty2', name: 'Access', isActive: true, isDefault: false },
  ],
  version: 'v9',
  fields: [
    { fieldId: 'f1', key: 'location', label: 'Location', helpText: null, type: 'ShortText', required: false, options: [], value: 'Glasgow Office', display: 'Glasgow Office', applicable: true, editable: true, active: true, missingRequired: false },
    { fieldId: 'f2', key: 'tag', label: 'Asset Number', helpText: null, type: 'ShortText', required: true, options: [], value: null, display: null, applicable: true, editable: true, active: true, missingRequired: true },
  ],
}

const SLA = {
  policyName: 'Standard',
  calendar: 'Office',
  timeZone: 'Europe/London',
  pauseWhenPending: true,
  appliedAt: '2026-10-01T09:00:00Z',
  firstResponse: { targetMinutes: 60, dueAt: '2026-10-01T10:00:00Z', metAt: '2026-10-01T09:18:00Z', pausedAt: null, state: 'Met', cycle: 1 },
  resolution: { targetMinutes: 480, dueAt: new Date(Date.now() - 43 * 60000).toISOString(), metAt: null, pausedAt: null, state: 'Breached', cycle: 1 },
  events: [],
}

const routes = (me = leadMe(), detail = makeDetail(), calls?: Call[]) => ({
  ...workspaceRoutes(me),
  'GET /api/tickets/IT-1': () => detail,
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/IT-1/properties': () => PROPS,
  'GET /api/tickets/IT-1/sla': () => SLA,
  'GET /api/tickets/t1/followers': () => ({ items: [{ userId: 'u2', displayName: 'Sarah Smith', addedAt: '2026-10-02T11:00:00Z' }], following: false }),
  'GET /api/tickets/t1/links': () => [{ id: 'l1', relationship: 'DuplicateOf', ticketId: 't2', reference: 'IT-1032', subject: 'VPN issue', status: 'Open' }],
  'PATCH /api/tickets/t1': (c: Call) => {
    calls?.push(c)
    return makeDetail({ priority: 'High', version: 'v2' })
  },
  'PATCH /api/tickets/IT-1/properties': (c: Call) => {
    calls?.push(c)
    return { ...PROPS, type: { id: 'ty2', displayName: 'Access' }, version: 'v10' }
  },
})

test('the properties panel gathers requester, core properties, SLA, fields, followers and related tickets, and the header keeps only identity, state and actions', async () => {
  mockFetch(routes(leadMe(), makeDetail({ requester: { id: 'r1', displayName: 'Dana Whitfield', email: 'dana@example.org', openTicketCount: 2, totalTicketCount: 16 } })))
  renderApp('/queue/my/IT-1')
  const panel = await screen.findByRole('complementary', { name: 'Ticket properties panel' })
  const requester = within(panel).getByRole('region', { name: 'Requester' })
  expect(within(requester).getByText('Dana Whitfield')).toBeInTheDocument()
  expect(within(requester).getByText('dana@example.org')).toBeInTheDocument()
  expect(requester).toHaveTextContent('2 open · 16 total')
  expect(within(requester).getByRole('link', { name: 'View history' })).toHaveAttribute('href', '/requesters/r1')

  const core = within(panel).getByRole('region', { name: 'Ticket properties' })
  expect(within(core).getByLabelText('Status')).toHaveValue('Open')
  expect(within(core).getByLabelText('Priority')).toHaveValue('Normal')
  expect(await within(core).findByLabelText('Type')).toHaveValue('ty1')

  const sla = await within(panel).findByRole('region', { name: 'Service level' })
  expect(sla).toHaveTextContent('Met in 18m')
  expect(sla).toHaveTextContent(/BREACHED · 4\dm ago/)

  const details = within(panel).getByRole('region', { name: 'Details' })
  expect(await within(details).findByText('Glasgow Office')).toBeInTheDocument()
  expect(within(details).getByText('Required · Not set')).toBeInTheDocument()
  expect(within(panel).getByText('Sarah Smith')).toBeInTheDocument()
  expect(within(await within(panel).findByRole('region', { name: 'Related tickets' })).getByRole('link', { name: /IT-1032/ })).toHaveAttribute('href', '/tickets/IT-1032')

  // The header no longer carries the property controls.
  const header = screen.getByRole('region', { name: 'Ticket' }).querySelector('header.t-head') as HTMLElement
  expect(within(header).queryByLabelText('Status')).not.toBeInTheDocument()
  expect(within(header).getByRole('button', { name: 'Macro' })).toBeInTheDocument()
})

test('status, priority and type change in place with the ticket version, and a refusal is explained', async () => {
  const calls: Call[] = []
  mockFetch(routes(leadMe(), makeDetail(), calls))
  renderApp('/queue/my/IT-1')
  const core = await screen.findByRole('region', { name: 'Ticket properties' })
  fireEvent.change(within(core).getByLabelText('Priority'), { target: { value: 'High' } })
  await waitFor(() => expect(calls.some((c) => c.path === '/api/tickets/t1')).toBe(true))
  expect(calls.find((c) => c.path === '/api/tickets/t1')!.body).toEqual({ priority: 'High', version: 'v1' })
  fireEvent.change(await within(core).findByLabelText('Type'), { target: { value: 'ty2' } })
  await waitFor(() => expect(calls.some((c) => c.path.endsWith('/properties'))).toBe(true))
  expect(calls.find((c) => c.path.endsWith('/properties'))!.body).toEqual({ version: 'v9', ticketTypeId: 'ty2' })
})

test('the panel can be hidden to reclaim width, is reopened from the header and the choice is remembered', async () => {
  mockFetch(routes())
  const first = renderApp('/queue/my/IT-1')
  const header = await screen.findByRole('button', { name: 'Properties' })
  expect(header).toHaveAttribute('aria-pressed', 'true')
  fireEvent.click(await screen.findByRole('button', { name: 'Hide properties' }))
  expect(screen.queryByRole('complementary', { name: 'Ticket properties panel' })).not.toBeInTheDocument()
  expect(window.localStorage.getItem('helpdesk.properties')).toBe('closed')
  first.unmount()

  mockFetch(routes())
  renderApp('/queue/my/IT-1')
  const again = await screen.findByRole('button', { name: 'Properties' })
  expect(again).toHaveAttribute('aria-pressed', 'false')
  expect(screen.queryByRole('complementary', { name: 'Ticket properties panel' })).not.toBeInTheDocument()
  fireEvent.click(again)
  expect(await screen.findByRole('complementary', { name: 'Ticket properties panel' })).toBeInTheDocument()
})

test('a merged ticket keeps the panel for context but every property is read-only', async () => {
  const merged = makeDetail({ mergedInto: { id: 't9', reference: 'IT-9', subject: 'Canonical', mergedAt: '2026-10-02T10:00:00Z', mergedBy: null } })
  mockFetch(routes(leadMe(), merged))
  renderApp('/queue/my/IT-1')
  const banner = await screen.findByRole('region', { name: 'Merged ticket' })
  expect(within(banner).getByRole('link', { name: 'Open IT-9' })).toBeInTheDocument()
  const panel = await screen.findByRole('complementary', { name: 'Ticket properties panel' })
  expect(within(panel).queryByLabelText('Status')).not.toBeInTheDocument()
  expect(within(panel).queryByLabelText('Assignee')).not.toBeInTheDocument()
  expect(within(panel).queryByRole('button', { name: 'Edit details' })).not.toBeInTheDocument()
  expect(within(panel).queryByRole('region', { name: 'Followers' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'More' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Macro' })).not.toBeInTheDocument()
})

test('callers without update access see values only, with no macro, more or edit controls', async () => {
  const viewer = makeMe({ memberships: [membership(['department.read', 'tickets.read'], 'Viewer')] })
  mockFetch(routes(viewer))
  renderApp('/queue/my/IT-1')
  const core = await screen.findByRole('region', { name: 'Ticket properties' })
  expect(within(core).queryByLabelText('Status')).not.toBeInTheDocument()
  expect(within(core).getByText('Open')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'More' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Macro' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Edit details' })).not.toBeInTheDocument()
})

test('the composer states who a public reply goes to and keeps the internal note unmistakable', async () => {
  mockFetch(routes(agentMe()))
  renderApp('/queue/my/IT-1')
  const form = await screen.findByRole('form', { name: 'Reply' })
  expect(within(form).getByText(/Public reply to/)).toHaveTextContent('Rita Requester <rita@example.test>')
  fireEvent.click(within(form).getByLabelText(/Internal note/))
  expect(within(form).getByText(/Internal note — not visible to the requester/)).toBeInTheDocument()
  expect(within(form).getByRole('button', { name: 'Add internal note' })).toBeInTheDocument()
})
