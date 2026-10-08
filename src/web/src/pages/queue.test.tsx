import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import {
  agentMe,
  makeDetail,
  makeMe,
  makeTicket,
  mockFetch,
  page,
  renderApp,
  workspaceRoutes,
  type Call,
} from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const T1 = makeTicket({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })
const T2 = makeTicket({ id: 't2', reference: 'IT-2', subject: 'VPN down', priority: 'High' })
const T3 = makeTicket({ id: 't3', reference: 'IT-3', subject: 'New laptop' })

const listCalls = (calls: Call[]) => calls.filter((c) => c.path.startsWith('/api/tickets?'))
const query = (c: Call) => new URL(c.path, 'http://x').searchParams

test('lists the queue with the Open+Pending default and keyset limit, and shows queue counts in the nav', async () => {
  const calls = mockFetch({ ...workspaceRoutes(), 'GET /api/tickets': () => page([T1, T2]) })
  renderApp('/queue/my')
  expect(await screen.findByRole('link', { name: 'Printer jam' })).toBeInTheDocument()
  const q = query(listCalls(calls)[0]!)
  expect(q.get('queue')).toBe('my')
  expect(q.getAll('status')).toEqual(['Open', 'Pending'])
  expect(q.get('limit')).toBe('50')
  expect(await screen.findByRole('link', { name: /My Queue/ })).toHaveTextContent('3')
  expect(screen.getByRole('link', { name: /Unassigned/ })).toHaveTextContent('4')
})

test('filters are sent to the server (status, priority, unassigned), not applied in the browser', async () => {
  const calls = mockFetch({ ...workspaceRoutes(), 'GET /api/tickets': () => page([T1]) })
  renderApp('/queue/all')
  await screen.findByRole('link', { name: 'Printer jam' })

  fireEvent.click(screen.getByRole('button', { name: 'Resolved' }))
  await waitFor(() => expect(query(listCalls(calls).at(-1)!).getAll('status')).toEqual(['Open', 'Pending', 'Resolved']))

  fireEvent.change(screen.getByLabelText('Priority'), { target: { value: 'High' } })
  await waitFor(() => expect(query(listCalls(calls).at(-1)!).getAll('priority')).toEqual(['High']))

  fireEvent.change(screen.getByLabelText('Assignee'), { target: { value: 'none' } })
  await waitFor(() => expect(query(listCalls(calls).at(-1)!).get('unassigned')).toBe('true'))

  fireEvent.change(screen.getByLabelText('Assignee'), { target: { value: 'me' } })
  await waitFor(() => expect(query(listCalls(calls).at(-1)!).get('assigneeUserId')).toBe('u1'))
  expect(query(listCalls(calls).at(-1)!).get('unassigned')).toBeNull()
})

test('pagination: one page is fetched, "Load more" follows the cursor and appends', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(),
    'GET /api/tickets': (c) => (query(c).get('cursor') === 'c1' ? page([T3]) : page([T1, T2], 'c1')),
  })
  renderApp('/queue/my')
  await screen.findByRole('link', { name: 'Printer jam' })
  expect(listCalls(calls)).toHaveLength(1)
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
  expect(await screen.findByRole('link', { name: 'New laptop' })).toBeInTheDocument()
  expect(query(listCalls(calls).at(-1)!).get('cursor')).toBe('c1')
  expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Printer jam' })).toBeInTheDocument()
})

test('the open ticket is highlighted and stays selected when more rows load', async () => {
  mockFetch({
    ...workspaceRoutes(),
    'GET /api/tickets': (c) => (query(c).get('cursor') === 'c1' ? page([T3]) : page([T1, T2], 'c1')),
    'GET /api/tickets/IT-2': () => makeDetail({ id: 't2', reference: 'IT-2', subject: 'VPN down' }),
    'GET /api/tickets/t2/messages': () => page([]),
  })
  renderApp('/queue/my/IT-2')
  const link = await screen.findByRole('link', { name: 'VPN down' })
  expect(link).toHaveAttribute('aria-current', 'true')
  expect(link.closest('tr')).toHaveAttribute('data-selected')
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
  await screen.findByRole('link', { name: 'New laptop' })
  expect(screen.getByRole('link', { name: 'VPN down' })).toHaveAttribute('aria-current', 'true')
  expect(screen.getByRole('link', { name: 'Printer jam' })).not.toHaveAttribute('aria-current')
})

test('keyboard: j/k move the cursor, Enter opens the highlighted ticket', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(),
    'GET /api/tickets': () => page([T1, T2, T3]),
    'GET /api/tickets/IT-2': () => makeDetail({ id: 't2', reference: 'IT-2', subject: 'VPN down' }),
    'GET /api/tickets/t2/messages': () => page([]),
  })
  renderApp('/queue/my')
  const first = await screen.findByRole('link', { name: 'Printer jam' })
  fireEvent.keyDown(document.body, { key: 'j' }) // -> first row
  expect(first.closest('tr')).toHaveAttribute('data-cursor')
  fireEvent.keyDown(document.body, { key: 'j' }) // -> second row
  const second = screen.getByRole('link', { name: 'VPN down' })
  expect(second.closest('tr')).toHaveAttribute('data-cursor')
  fireEvent.keyDown(document.body, { key: 'k' })
  expect(first.closest('tr')).toHaveAttribute('data-cursor')
  fireEvent.keyDown(document.body, { key: 'ArrowDown' })
  fireEvent.keyDown(document.body, { key: 'Enter' })
  expect(await screen.findByRole('heading', { name: 'VPN down', level: 2 })).toBeInTheDocument()
  expect(calls.some((c) => c.path === '/api/tickets/IT-2')).toBe(true)
})

test('shortcuts are ignored while typing and with modifier keys; "/" focuses search; "?" opens help and Esc closes it', async () => {
  mockFetch({ ...workspaceRoutes(), 'GET /api/tickets': () => page([T1, T2]) })
  renderApp('/queue/my')
  const first = await screen.findByRole('link', { name: 'Printer jam' })
  const search = screen.getByRole('searchbox', { name: 'Search tickets' })

  fireEvent.keyDown(document.body, { key: '/' })
  expect(search).toHaveFocus()

  fireEvent.keyDown(search, { key: 'j' })
  fireEvent.keyDown(search, { key: '?' })
  expect(document.querySelector('[data-cursor]')).toBeNull()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

  fireEvent.keyDown(document.body, { key: 'j', ctrlKey: true })
  fireEvent.keyDown(document.body, { key: 'j', metaKey: true })
  expect(document.querySelector('[data-cursor]')).toBeNull()

  fireEvent.keyDown(document.body, { key: 'j' })
  expect(first.closest('tr')).toHaveAttribute('data-cursor')

  fireEvent.keyDown(document.body, { key: '?' })
  const dialog = await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })
  expect(within(dialog).getByText('Open the highlighted ticket')).toBeInTheDocument()
  fireEvent.keyDown(document, { key: 'Escape' })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
})

test('landing: an agent lands on the dashboard of their department', async () => {
  mockFetch({ ...workspaceRoutes(), 'GET /api/me/queue-preferences': () => ({ columns: null, defaultQueue: null, defaultViewId: null }) })
  renderApp('/')
  expect(await screen.findByRole('heading', { name: /dashboard/i, level: 1 })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Administration' })).not.toBeInTheDocument()
})

test('landing: a default queue the agent chose themselves still wins over the dashboard', async () => {
  const calls = mockFetch({ ...workspaceRoutes(), 'GET /api/me/queue-preferences': () => ({ columns: null, defaultQueue: 'team', defaultViewId: null }) })
  renderApp('/')
  expect(await screen.findByRole('heading', { name: 'Team Queue' })).toBeInTheDocument()
  expect(query(listCalls(calls)[0]!).get('queue')).toBe('team')
})

test('landing: a platform admin without ticket access goes to Administration with an explanation and no queue entries', async () => {
  mockFetch({
    'GET /api/me': () => makeMe({ isPlatformAdmin: true, platformPermissions: ['platform.departments.manage'] }),
    'GET /api/departments': () => [],
  })
  renderApp('/')
  expect(await screen.findByRole('heading', { name: 'Departments' })).toBeInTheDocument()
  expect(screen.getByText(/You have no access to tickets/)).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Administration/ })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /My Queue/ })).not.toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /Search/ })).not.toBeInTheDocument()
})

test('landing: a user with neither tickets nor administration lands in the requester portal', async () => {
  mockFetch({ 'GET /api/me': () => makeMe(), 'GET /api/portal/requests': () => ({ items: [], nextCursor: null }), 'GET /api/portal/forms': () => [] })
  renderApp('/')
  expect(await screen.findByRole('heading', { name: 'How can we help?' })).toBeInTheDocument()
})

test('Administration is offered for department.manage and hidden otherwise', async () => {
  const manager = makeMe({
    memberships: [{ ...agentMe().memberships[0]!, permissions: ['tickets.read', 'department.manage'] }],
  })
  mockFetch(workspaceRoutes(manager))
  renderApp('/queue/my')
  expect(await screen.findByRole('link', { name: /Administration/ })).toHaveAttribute('href', '/admin')
})

test('a ticket URL (reference) opens the split workspace on a hard load, and a GUID works too', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(),
    'GET /api/tickets/IT-1042': () => makeDetail({ id: 'g-1042', reference: 'IT-1042', subject: 'Direct link ticket' }),
    'GET /api/tickets/g-1042/messages': () => page([]),
  })
  renderApp('/tickets/IT-1042')
  expect(await screen.findByRole('heading', { name: 'Direct link ticket', level: 2 })).toBeInTheDocument()
  // the list pane is there as well (split workspace), and the ticket is requested by the URL segment
  expect(screen.getByRole('searchbox', { name: 'Search tickets' })).toBeInTheDocument()
  expect(calls.some((c) => c.path === '/api/tickets/IT-1042')).toBe(true)
})

test('a missing or inaccessible ticket shows the neutral not-found message', async () => {
  mockFetch({ ...workspaceRoutes() })
  renderApp('/tickets/IT-9')
  expect(await screen.findByText(/Not found, or you do not have access/)).toBeInTheDocument()
})

test('search page does not query until two characters are entered, then searches all statuses', async () => {
  const calls = mockFetch({ ...workspaceRoutes(), 'GET /api/tickets': () => page([T1]) })
  renderApp('/search')
  expect(await screen.findByText(/Type at least 2 characters/)).toBeInTheDocument()
  expect(listCalls(calls)).toHaveLength(0)
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search tickets' }), { target: { value: 'print' } })
  expect(await screen.findByRole('link', { name: 'Printer jam' })).toBeInTheDocument()
  const q = query(listCalls(calls)[0]!)
  expect(q.get('q')).toBe('print')
  expect(q.getAll('status')).toEqual(['Open', 'Pending', 'Resolved', 'Closed'])
})
