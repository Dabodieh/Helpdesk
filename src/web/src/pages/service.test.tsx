import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { SlaBadge } from '../components/Sla'
import { dueIn, formatMinutes } from '../lib/sla'
import { csrfHandler, DEPT, json, makeMe, membership, mockFetch, renderApp } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const ADMIN_PERMS = ['department.read', 'department.manage', 'department.sla.manage', 'department.automation.manage']
const adminMe = makeMe({ memberships: [membership(ADMIN_PERMS, 'DepartmentAdmin')] })
const plainAdminMe = makeMe({ memberships: [membership(['department.read', 'department.manage'], 'DepartmentAdmin')] })

const CALENDAR = {
  id: 'c1',
  departmentId: 'd1',
  name: 'Office',
  timeZone: 'Europe/London',
  isActive: true,
  weekly: [{ day: 'Monday', intervals: [{ start: '09:00', end: '17:00' }] }],
  exceptions: [],
  updatedAt: '2026-01-01T00:00:00Z',
  version: 'v1',
}
const POLICY = {
  id: 'p1',
  departmentId: 'd1',
  name: 'Standard',
  isActive: true,
  order: 10,
  conditions: [],
  calendarId: 'c1',
  calendarName: 'Office',
  firstResponseMinutes: 60,
  resolutionMinutes: 480,
  pauseWhenPending: true,
  approachingLeadMinutes: 60,
  updatedAt: '2026-01-01T00:00:00Z',
  version: 'v1',
}
const RULE = {
  id: 'r1',
  departmentId: 'd1',
  name: 'Network routing',
  isActive: true,
  trigger: 'TicketCreated',
  order: 10,
  stopProcessing: false,
  conditions: [{ field: 'subjectContains', operator: 'contains', values: ['network'] }],
  actions: [{ type: 'assignAgent', value: 'u9', text: null }],
  updatedAt: '2026-01-01T00:00:00Z',
  version: 'v1',
}

const routes = (me = adminMe) => ({
  'GET /api/me': () => me,
  'GET /api/departments/d1': () => DEPT,
  'GET /api/departments/d1/members': () => [{ userId: 'u9', displayName: 'Network Agent', email: null, role: 'Agent' }],
  'GET /api/departments/d1/mailboxes': () => [],
  'GET /api/departments/d1/business-hours': () => [CALENDAR],
  'GET /api/departments/d1/sla-policies': () => [POLICY],
  'GET /api/departments/d1/automations': () => [RULE],
  ...csrfHandler,
})

test('SLA badge uses a word and a mark, never colour alone, and says nothing without an SLA', () => {
  const { rerender } = render(<SlaBadge sla={{ state: 'Breached', dueAt: new Date(Date.now() - 2 * 3600_000).toISOString(), clock: 'Resolution', firstResponse: 'Met', resolution: 'Breached' }} />)
  expect(screen.getByText(/Breached/)).toBeInTheDocument()
  expect(screen.getByText(/2h overdue/)).toBeInTheDocument()
  rerender(<SlaBadge sla={null} />)
  expect(screen.queryByText(/Breached/)).not.toBeInTheDocument()
  rerender(<SlaBadge sla={{ state: 'Paused', dueAt: null, clock: null, firstResponse: 'None', resolution: 'Paused' }} />)
  expect(screen.getByText(/Paused/)).toBeInTheDocument()
})

test('deadline and target wording', () => {
  const now = Date.parse('2026-01-12T10:00:00Z')
  expect(dueIn('2026-01-12T10:25:00Z', now)).toBe('in 25m')
  expect(dueIn('2026-01-12T07:00:00Z', now)).toBe('3h overdue')
  expect(dueIn('2026-01-14T10:00:00Z', now)).toBe('in 2d')
  expect(formatMinutes(90)).toBe('1h 30m')
  expect(formatMinutes(1440 + 120)).toBe('1d 2h')
})

test('department administrators with the SLA and automation permissions get the three new areas', async () => {
  mockFetch(routes())
  renderApp('/admin/departments/d1')
  expect(await screen.findByRole('tab', { name: 'Business hours' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'SLA policies' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Automations' })).toBeInTheDocument()
})

test('without those permissions the areas are not offered', async () => {
  mockFetch(routes(plainAdminMe))
  renderApp('/admin/departments/d1')
  await screen.findByRole('tab', { name: 'Members' })
  expect(screen.queryByRole('tab', { name: 'Business hours' })).not.toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: 'SLA policies' })).not.toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: 'Automations' })).not.toBeInTheDocument()
})

test('creating business hours posts structured weekly hours, not JSON text', async () => {
  const calls = mockFetch({ ...routes(), 'POST /api/departments/d1/business-hours': () => json(CALENDAR, 201) })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Business hours' }))
  fireEvent.click(await screen.findByRole('button', { name: 'New business hours' }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Day shift' } })
  fireEvent.change(within(dialog).getByLabelText('Time zone'), { target: { value: 'Europe/London' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Add exception' }))
  fireEvent.change(within(dialog).getByLabelText('Exception 1 date'), { target: { value: '2026-12-25' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save business hours' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/business-hours'))).toBe(true))
  const body = calls.find((c) => c.method === 'POST')!.body as { name: string; weekly: { day: string }[]; exceptions: { date: string; intervals: unknown[] }[] }
  expect(body.name).toBe('Day shift')
  expect(body.weekly.map((w) => w.day)).toEqual(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'])
  expect(body.exceptions).toEqual([{ date: '2026-12-25', name: '', intervals: [] }])
})

test('SLA policies list shows order, targets and hours, and a policy can be created with a target', async () => {
  const calls = mockFetch({ ...routes(), 'POST /api/departments/d1/sla-policies': () => json(POLICY, 201) })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'SLA policies' }))
  expect(await (await screen.findAllByText('Standard'))[0]).toBeInTheDocument()
  expect(screen.getByText(/First response 1h/)).toBeInTheDocument()
  expect(screen.getByText(/Resolution 8h/)).toBeInTheDocument()
  expect(screen.getByText(/pauses when Pending/)).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'New SLA policy' }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Urgent' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Add condition' }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/sla-policies'))).toBe(true))
  const body = calls.find((c) => c.method === 'POST')!.body as { firstResponseMinutes: number; resolutionMinutes: number; conditions: unknown[] }
  expect(body.firstResponseMinutes).toBe(60)
  expect(body.resolutionMinutes).toBe(480)
  expect(body.conditions).toHaveLength(1)
})

test('automation rules read in plain language with names, and "Test against ticket" explains without changing anything', async () => {
  const calls = mockFetch({
    ...routes(),
    'POST /api/departments/d1/automations/test': () =>
      json({
        ticket: 'IT-7',
        trigger: 'TicketCreated',
        wouldChangeAnything: true,
        notice: 'Nothing was changed.',
        rules: [
          {
            ruleId: 'r1',
            name: 'Network routing',
            trigger: 'TicketCreated',
            evaluated: true,
            matched: true,
            stopsProcessing: false,
            conditions: [{ field: 'subjectContains', operator: 'contains', values: ['network'], actual: null, result: true }],
            actions: [{ index: 0, type: 'assignAgent', value: 'u9', result: 'WouldChange', detail: 'Assignee would change' }],
            note: null,
          },
        ],
      }),
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Automations' }))
  expect(await (await screen.findAllByText('Network routing'))[0]).toBeInTheDocument()
  expect(await screen.findByText(/Assign agent: Network Agent/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Test' }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText('Ticket reference'), { target: { value: 'IT-7' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Test' }))
  const result = await within(dialog).findByRole('region', { name: 'Test result' })
  expect(within(result).getByText('Matched')).toBeInTheDocument()
  expect(within(result).getByText('Would change')).toBeInTheDocument()
  expect(within(result).getByText(/Nothing was changed/)).toBeInTheDocument()
  const post = calls.find((c) => c.method === 'POST' && c.path.endsWith('/automations/test'))!
  expect(post.body).toEqual({ ticket: 'IT-7', trigger: 'TicketCreated', ruleId: 'r1' })
  // The dry run only POSTs to the test endpoint: no rule or ticket was written.
  expect(calls.filter((c) => c.method !== 'GET' && !c.path.includes('csrf')).map((c) => c.path)).toEqual(['/api/departments/d1/automations/test'])
})

test('rule editor offers only structured controls and server field errors are shown next to the action', async () => {
  mockFetch({
    ...routes(),
    'POST /api/departments/d1/automations': () =>
      new Response(JSON.stringify({ status: 400, title: 'Validation failed', errors: { 'actions[0]': ['The assignee is not a member of this department.'] } }), {
        status: 400,
        headers: { 'content-type': 'application/problem+json' },
      }),
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Automations' }))
  fireEvent.click(await screen.findByRole('button', { name: 'New rule' }))
  const dialog = await screen.findByRole('dialog')
  expect(within(dialog).queryByRole('textbox', { name: /json/i })).not.toBeInTheDocument()
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Bad' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save rule' }))
  expect(await within(dialog).findByText('The assignee is not a member of this department.')).toBeInTheDocument()
})
