import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { makeDetail, makeMessage, makeTicket, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const CANDIDATE = { id: 't9', reference: 'IT-1047', subject: 'VPN disconnecting', status: 'Open', requesterName: 'Vic', requesterEmail: 'vic@example.test', version: 'v9' }

const routes = (detail = makeDetail()) => ({
  ...workspaceRoutes(),
  'GET /api/tickets/IT-1': () => detail,
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
})

test('a merged ticket says so, links to the canonical ticket and offers no editing, replying or merge actions', async () => {
  const merged = makeDetail({
    mergedInto: { id: 't9', reference: 'IT-1047', subject: 'VPN disconnecting', mergedAt: '2026-10-02T10:00:00Z', mergedBy: { id: 'u2', displayName: 'Alice' } },
  })
  mockFetch(routes(merged))
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  const banner = await within(pane).findByRole('region', { name: 'Merged ticket' })
  expect(within(banner).getByText('MERGED')).toBeInTheDocument()
  expect(within(banner).getByText(/was merged into/)).toBeInTheDocument()
  expect(within(banner).getByRole('link', { name: 'Open IT-1047' })).toHaveAttribute('href', '/tickets/IT-1047')
  expect(within(pane).queryByRole('button', { name: 'More' })).not.toBeInTheDocument()
  expect(within(pane).queryByRole('button', { name: 'Edit subject' })).not.toBeInTheDocument()
  expect(within(pane).queryByRole('button', { name: /Send|Add note/ })).not.toBeInTheDocument()
  expect(within(pane).getByText(/Replies and notes are disabled/)).toBeInTheDocument()
})

test('merge flow: pick the canonical ticket, review who stays, confirm; the request names the explicit canonical ticket', async () => {
  const calls = mockFetch({
    ...routes(),
    'GET /api/tickets/t1/link-candidates?q=VPN': () => [CANDIDATE],
    'POST /api/tickets/t9/merge': () => makeDetail({ id: 't9', reference: 'IT-1047' }),
  })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'More' }))
  fireEvent.click(screen.getByRole('button', { name: 'Merge ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'Merge ticket' })
  fireEvent.change(within(dialog).getByLabelText('Find a ticket by reference or subject'), { target: { value: 'VPN' } })
  fireEvent.click(await within(dialog).findByRole('radio', { name: 'IT-1047 VPN disconnecting' }))

  const preview = within(dialog).getByRole('region', { name: 'Merge preview' })
  expect(preview).toHaveTextContent('Keep:IT-1047 — VPN disconnecting')
  expect(preview).toHaveTextContent('Merge into it:IT-1 — Printer jam')
  expect(within(preview).getByText(/route future strongly correlated replies to IT-1047/)).toBeInTheDocument()

  fireEvent.click(within(dialog).getByRole('button', { name: 'Merge tickets' }))
  await vi.waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/tickets/t9/merge')).toBe(true))
  const post = calls.find((c) => c.path === '/api/tickets/t9/merge')!
  expect(post.body).toEqual({ version: 'v9', sources: [{ ticketId: 't1', version: 'v1' }] })
})

test('related tickets show relationship, reference, subject and status and open in the workspace; linking posts the chosen relationship', async () => {
  const calls = mockFetch({
    ...routes(),
    'GET /api/tickets/t1/links': () => [
      { id: 'l1', relationship: 'DuplicateOf', ticketId: 't2', reference: 'IT-1042', subject: 'VPN connection issue', status: 'Open' },
      { id: 'l2', relationship: 'Related', ticketId: 't3', reference: 'IT-992', subject: 'Home broadband issue', status: 'Closed' },
      { id: 'l3', relationship: 'Child', ticketId: 't4', reference: 'IT-1061', subject: 'Replace laptop certificate', status: 'Pending', mergedInto: 'IT-1070' },
    ],
    'GET /api/tickets/t1/link-candidates?q=cert': () => [{ ...CANDIDATE, id: 't5', reference: 'IT-77', subject: 'Certificate expired' }],
    'POST /api/tickets/t1/links': () => ({ id: 'l9', relationship: 'Parent', ticketId: 't5', reference: 'IT-77', subject: 'Certificate expired', status: 'Open' }),
  })
  renderApp('/queue/my/IT-1')
  const related = await screen.findByRole('region', { name: 'Related tickets' })
  expect(within(related).getByText('Duplicate of')).toBeInTheDocument()
  expect(within(related).getByRole('link', { name: /IT-1042/ })).toHaveAttribute('href', '/tickets/IT-1042')
  expect(within(related).getByText('Closed')).toBeInTheDocument()
  expect(within(related).getByText('Merged into IT-1070')).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'More' }))
  fireEvent.click(screen.getByRole('button', { name: 'Link ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'Link ticket' })
  fireEvent.change(within(dialog).getByLabelText('Find a ticket by reference or subject'), { target: { value: 'cert' } })
  fireEvent.click(await within(dialog).findByRole('radio', { name: 'IT-77 Certificate expired' }))
  fireEvent.change(within(dialog).getByLabelText('The selected ticket is'), { target: { value: 'Parent' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Link ticket' }))
  await vi.waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/tickets/t1/links')).toBe(true))
  expect(calls.find((c) => c.path === '/api/tickets/t1/links' && c.method === 'POST')!.body).toEqual({ ticketId: 't5', type: 'Parent' })
})

test('search results mark a merged ticket with where it went', async () => {
  mockFetch({ ...workspaceRoutes(), 'GET /api/tickets': () => page([makeTicket({ id: 't2', reference: 'IT-1051', subject: 'VPN not working', mergedInto: 'IT-1047' })]) })
  renderApp('/queue/all')
  expect(await screen.findByText('Merged into IT-1047')).toBeInTheDocument()
})
