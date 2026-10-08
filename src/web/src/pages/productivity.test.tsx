import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { summarise } from '../lib/bulk'
import { clampWidth, SPLIT_MIN } from '../lib/split'
import { agentMe, json, makeDetail, makeMessage, makeTicket, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const TICKET = makeDetail({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })
const CANNED = {
  id: 'c1',
  scope: 'Department',
  departmentId: 'd1',
  departmentName: 'IT Support',
  title: 'Reset password',
  body: 'Hello {{requester.name}},\nPlease reset your password at the portal.',
  isActive: true,
  canManage: false,
  updatedAt: '2026-01-01T00:00:00Z',
  version: 'v1',
}
const routes = () => ({
  ...workspaceRoutes(agentMe()),
  'GET /api/tickets/IT-1': () => TICKET,
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/canned-responses': () => json([CANNED]),
  'GET /api/tickets/t1/canned-responses/c1/render': () =>
    json({ id: 'c1', title: 'Reset password', body: 'Hello Ana,\nPlease reset your password at the portal.', emptyPlaceholders: [] }),
})

test('canned response: picking inserts the text rendered for this ticket into the draft and posts nothing', async () => {
  const calls = mockFetch(routes())
  renderApp('/queue/my/IT-1')
  await screen.findByRole('heading', { name: 'Printer jam', level: 2 })
  const box = screen.getByRole('textbox', { name: 'Public reply' })
  fireEvent.change(box, { target: { value: 'Hi there. ' } })
  fireEvent.click(screen.getByRole('button', { name: /Insert response/ }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.click(await within(dialog).findByRole('button', { name: /Reset password/ }))
  await waitFor(() => expect(box).toHaveValue('Hi there. Hello Ana,\nPlease reset your password at the portal.'))
  expect((box as HTMLTextAreaElement).value).not.toContain('{{')
  expect(calls.some((c) => c.path === '/api/tickets/t1/canned-responses/c1/render')).toBe(true)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls.some((c) => c.method === 'POST')).toBe(false)
})

test('queue rows can be selected and the bulk bar appears', async () => {
  mockFetch({ ...workspaceRoutes(agentMe()), 'GET /api/tickets': () => page([makeTicket({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })]) })
  renderApp('/queue/my')
  await screen.findByRole('link', { name: 'Printer jam' })
  fireEvent.click(screen.getByRole('checkbox', { name: /Select IT-1/ }))
  expect(await screen.findByText(/1 selected/)).toBeInTheDocument()
})

test('bulk apply sends the version of every selected ticket and says which ones changed since', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(agentMe()),
    'GET /api/tickets': () => page([makeTicket({ id: 't1', reference: 'IT-1', subject: 'Printer jam', version: 'v7' })]),
    'POST /api/tickets/bulk': () => ({ results: [{ ticketId: 't1', outcome: 'changed', code: 'version_conflict' }], updated: 0, unchanged: 0, failed: 1 }),
  })
  renderApp('/queue/my')
  await screen.findByRole('link', { name: 'Printer jam' })
  fireEvent.click(screen.getByRole('checkbox', { name: /Select IT-1/ }))
  fireEvent.change(within(await screen.findByRole('region', { name: 'Bulk actions' })).getByLabelText('Priority'), { target: { value: 'High' } })
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }))
  expect(await screen.findByText(/changed since you selected them/)).toBeInTheDocument()
  const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/tickets/bulk')!.body as { versions?: Record<string, string> }
  expect(sent.versions).toEqual({ t1: 'v7' })
})

test('bulk summary explains partial failures in plain words', () => {
  const text = summarise({
    updated: 2,
    unchanged: 1,
    failed: 2,
    results: [
      { ticketId: 'a', outcome: 'updated' },
      { ticketId: 'b', outcome: 'not_found' },
      { ticketId: 'c', outcome: 'forbidden' },
    ],
  } as never)
  expect(text).toContain('2 updated')
  expect(text).toContain('1 already up to date')
  expect(text).toContain('not found or not accessible')
  expect(text).toContain('not allowed')
})

test('split width is clamped: queue never below the minimum and ticket keeps room', () => {
  expect(clampWidth(100, 1400)).toBe(SPLIT_MIN)
  expect(clampWidth(5000, 1400)).toBe(940)
  expect(clampWidth(600, 1400)).toBe(600)
})
