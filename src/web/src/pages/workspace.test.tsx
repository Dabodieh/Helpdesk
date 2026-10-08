import { SENT } from '../components/Conversation'
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { applyPresence } from '../lib/realtime'
import { afterEach, expect, test, vi } from 'vitest'
import {
  agentMe,
  json,
  leadMe,
  makeDetail,
  makeMessage,
  mockFetch,
  page,
  problem,
  renderApp,
  viewerMe,
  workspaceRoutes,
  type Call,
} from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const TICKET = makeDetail({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })
const ticketRoutes = (me = agentMe(), detail = TICKET) => ({
  ...workspaceRoutes(me),
  'GET /api/tickets/IT-1': () => detail,
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
})
const openTicket = async () => {
  renderApp('/queue/my/IT-1')
  await screen.findByRole('heading', { name: 'Printer jam', level: 2 })
}
/** Controls of the open ticket (the queue toolbar has same-named filters). */
const pane = () => within(screen.getByRole('region', { name: 'Ticket' }))
const posts = (calls: Call[], suffix: string) => calls.filter((c) => c.method === 'POST' && c.path.endsWith(suffix))

test('permission-aware controls: a viewer sees values only, no composer and no edit controls', async () => {
  mockFetch(ticketRoutes(viewerMe()))
  await openTicket()
  expect(pane().queryByRole('combobox', { name: 'Status' })).not.toBeInTheDocument()
  expect(pane().queryByRole('combobox', { name: 'Priority' })).not.toBeInTheDocument()
  expect(pane().queryByRole('combobox', { name: 'Team' })).not.toBeInTheDocument()
  expect(pane().queryByRole('combobox', { name: 'Assignee' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Edit subject/ })).not.toBeInTheDocument()
  expect(screen.getByText(/You can read this ticket but not reply/)).toBeInTheDocument()
  expect(screen.queryByRole('textbox', { name: /reply|note/i })).not.toBeInTheDocument()
})

test('permission-aware controls: an agent edits status, priority and team (D21), the assignee stays limited to themself', async () => {
  mockFetch(ticketRoutes(agentMe()))
  await openTicket()
  expect(pane().getByRole('combobox', { name: 'Status' })).toBeEnabled()
  expect(pane().getByRole('combobox', { name: 'Priority' })).toBeEnabled()
  expect(pane().getByRole('combobox', { name: 'Team' })).toBeEnabled() // routing is open to agents (tickets.assign.team)
  // unassigned ticket: assign.self allows taking it
  const assignee = pane().getByRole('combobox', { name: 'Assignee' })
  expect(within(assignee).getByRole('option', { name: /\(me\)/ })).toBeInTheDocument()
})

test('resolving from the properties rail warns that the requester gets no message, and can send the agent to the reply box first (D24)', async () => {
  const calls = mockFetch({ ...ticketRoutes(), 'PATCH /api/tickets/t1': (c) => makeDetail({ ...(c.body as object), version: 'v2' }) })
  await openTicket()
  fireEvent.change(pane().getByRole('combobox', { name: 'Status' }), { target: { value: 'Resolved' } })
  const dialog = await screen.findByRole('dialog', { name: /Resolve without a reply/ })
  expect(calls.some((c) => c.method === 'PATCH')).toBe(false) // nothing happens before the choice
  fireEvent.click(within(dialog).getByRole('button', { name: 'Write a reply first' }))
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls.some((c) => c.method === 'PATCH')).toBe(false)

  fireEvent.change(pane().getByRole('combobox', { name: 'Status' }), { target: { value: 'Resolved' } })
  fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Resolve without a message' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
  expect(calls.find((c) => c.method === 'PATCH')!.body).toMatchObject({ status: 'Resolved' })
})

test('permission-aware controls: assign.self cannot reassign a ticket owned by someone else', async () => {
  mockFetch(ticketRoutes(agentMe(), makeDetail({ assignee: { id: 'u9', displayName: 'Other Agent' } })))
  await openTicket()
  expect(pane().queryByRole('combobox', { name: 'Assignee' })).not.toBeInTheDocument()
  expect(screen.getAllByText('Other Agent').length).toBeGreaterThan(0)
})

test('status and priority changes are PATCHed with the loaded version', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'PATCH /api/tickets/t1': (c) => makeDetail({ ...(c.body as object), version: 'v2' }),
  })
  await openTicket()
  fireEvent.change(pane().getByRole('combobox', { name: 'Status' }), { target: { value: 'Pending' } })
  await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ version: 'v1', status: 'Pending' }))
  await waitFor(() => expect(pane().getByRole('combobox', { name: 'Status' })).toHaveValue('Pending'))
  fireEvent.change(pane().getByRole('combobox', { name: 'Priority' }), { target: { value: 'Urgent' } })
  await waitFor(() => expect(calls.filter((c) => c.method === 'PATCH').at(-1)?.body).toEqual({ version: 'v2', priority: 'Urgent' }))
})

test('a team lead replaces team and assignee through PUT assignment with version', async () => {
  const calls = mockFetch({
    ...ticketRoutes(leadMe()),
    'GET /api/departments/d1/teams': () => [{ id: 'tm1', name: 'Tier 1', isActive: true, version: 'a' }],
    'GET /api/departments/d1/members': () => [
      { userId: 'u9', displayName: 'Other Agent', email: null, role: 'Agent', teamIds: [] },
      { userId: 'u8', displayName: 'View Only', email: null, role: 'Viewer', teamIds: [] },
    ],
    'PUT /api/tickets/t1/assignment': (c) => makeDetail({ ...(c.body as object), version: 'v2' }),
  })
  await openTicket()
  const assignee = pane().getByRole('combobox', { name: 'Assignee' })
  await waitFor(() => expect(within(assignee).getByRole('option', { name: 'Other Agent' })).toBeInTheDocument())
  expect(within(assignee).queryByRole('option', { name: 'View Only' })).not.toBeInTheDocument()
  fireEvent.change(assignee, { target: { value: 'u9' } })
  await waitFor(() =>
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ version: 'v1', teamId: null, assigneeUserId: 'u9' }),
  )
  const team = pane().getByRole('combobox', { name: 'Team' })
  await waitFor(() => expect(within(team).getByRole('option', { name: 'Tier 1' })).toBeInTheDocument())
})

test('composer: internal mode shows the banner and submits kind Internal', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/messages': (c) =>
      json(
        {
          message: makeMessage({
            id: 'm2',
            kind: 'Internal',
            authorType: 'Agent',
            author: { id: 'u1', displayName: 'Test User' },
            body: (c.body as { body: string }).body,
          }),
          ticket: makeDetail({ version: 'v2' }),
        },
        201,
      ),
  })
  await openTicket()
  expect(screen.getByRole('radio', { name: 'Public reply' })).toBeChecked()
  expect(screen.queryByText('Internal note — not visible to the requester')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Post public reply' })).toBeDisabled()

  fireEvent.click(screen.getByRole('radio', { name: 'Internal note' }))
  expect(screen.getByText('Internal note — not visible to the requester')).toBeInTheDocument()
  fireEvent.change(screen.getByRole('textbox', { name: 'Internal note' }), { target: { value: 'Checked the fuser\nlooks worn' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add internal note' }))
  await waitFor(() => expect(posts(calls, '/messages')).toHaveLength(1))
  expect(posts(calls, '/messages')[0]!.body).toMatchObject({ kind: 'Internal', body: 'Checked the fuser\nlooks worn' })
  expect(posts(calls, '/messages')[0]!.headers['X-CSRF-TOKEN']).toBe('tok')
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Internal note' })).toHaveValue(''))
})

test('keyboard: r focuses the public reply, n the internal note; Esc leaves the box', async () => {
  mockFetch(ticketRoutes())
  await openTicket()
  fireEvent.keyDown(document.body, { key: 'n' })
  const note = screen.getByRole('textbox', { name: 'Internal note' })
  expect(note).toHaveFocus()
  fireEvent.keyDown(note, { key: 'r' }) // typing: ignored
  expect(screen.getByRole('textbox', { name: 'Internal note' })).toHaveFocus()
  fireEvent.keyDown(note, { key: 'Escape' })
  expect(note).not.toHaveFocus()
  fireEvent.keyDown(document.body, { key: 'r' })
  expect(screen.getByRole('textbox', { name: 'Public reply' })).toHaveFocus()
})

test('public reply: payload carries status-after and the entry says it was recorded, not emailed', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/messages': (c) =>
      json(
        {
          message: makeMessage({
            id: 'm2',
            authorType: 'Agent',
            author: { id: 'u1', displayName: 'Test User' },
            body: (c.body as { body: string }).body,
            deliveryState: 'NotDelivered',
          }),
          ticket: makeDetail({ status: 'Resolved', version: 'v2' }),
        },
        201,
      ),
    'GET /api/tickets/t1/messages': [
      () => page([makeMessage()]),
      () =>
        page([
          makeMessage({ id: 'm2', authorType: 'Agent', author: { id: 'u1', displayName: 'Test User' }, body: 'Try the other tray', deliveryState: 'NotDelivered' }),
          makeMessage(),
        ]),
    ],
  })
  await openTicket()
  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'Try the other tray' } })
  fireEvent.change(pane().getByRole('combobox', { name: /Set status after/ }), { target: { value: 'Resolved' } })
  fireEvent.click(screen.getByRole('button', { name: 'Post public reply' }))
  await waitFor(() => expect(posts(calls, '/messages')).toHaveLength(1))
  expect(posts(calls, '/messages')[0]!.body).toMatchObject({ kind: 'Public', body: 'Try the other tray', status: 'Resolved' })
  const label = await screen.findByText('Recorded, not emailed (the ticket’s mailbox is not available to send from)')
  expect(label).toBeInTheDocument()
  // never claims a send
  expect(screen.queryByText(SENT)).not.toBeInTheDocument() // the conversation's delivery line, not unrelated words such as the queue filter "Reply not sent"
  expect(screen.queryByText(/delivered to/i)).not.toBeInTheDocument()
})

test('conversation: ascending order, load earlier follows the cursor, bodies are plain text with line breaks', async () => {
  const m1 = makeMessage({ id: 'm1', body: 'first message', createdAt: '2026-10-01T09:00:00Z' })
  const m2 = makeMessage({ id: 'm2', body: '<b>second</b>\nline two', createdAt: '2026-10-01T10:00:00Z' })
  const m3 = makeMessage({ id: 'm3', kind: 'Internal', authorType: 'Agent', author: { id: 'u2', displayName: 'Ann Agent' }, body: 'private thought', createdAt: '2026-10-01T11:00:00Z' })
  const calls = mockFetch({
    ...ticketRoutes(),
    'GET /api/tickets/t1/messages': (c) =>
      new URL(c.path, 'http://x').searchParams.get('cursor') === 'older' ? page([m1]) : page([m3, m2], 'older'),
  })
  await openTicket()
  await screen.findByText('private thought')
  const articles = screen.getAllByRole('article')
  expect(articles.map((a) => a.textContent)).toEqual([
    expect.stringContaining('<b>second</b>'),
    expect.stringContaining('private thought'),
  ])
  // plain text: the markup is shown literally, nothing is parsed
  expect(document.querySelector('.msg-body b')).toBeNull()
  expect(articles[1]).toHaveAttribute('aria-label', 'Internal note from Ann Agent')
  expect(within(articles[1]!).getByText('Not visible to the requester')).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'Load earlier messages' }))
  await screen.findByText('first message')
  expect(new URL(calls.filter((c) => c.path.includes('/messages?')).at(-1)!.path, 'http://x').searchParams.get('cursor')).toBe('older')
  expect(screen.getAllByRole('article')[0]).toHaveTextContent('first message')
})

test('version conflict: banner with reload; the typed draft survives and the ticket is refetched', async () => {
  let fresh = false
  const calls = mockFetch({
    ...ticketRoutes(),
    'GET /api/tickets/IT-1': () => (fresh ? makeDetail({ status: 'Resolved', version: 'v9' }) : TICKET),
    'PATCH /api/tickets/t1': () => problem(409, 'Conflict', { code: 'version_conflict', detail: 'whatever text' }),
  })
  await openTicket()
  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'my half written reply' } })
  fireEvent.change(pane().getByRole('combobox', { name: 'Status' }), { target: { value: 'Pending' } })
  const alert = await screen.findByText(/Someone else changed this ticket/)
  expect(alert).toBeInTheDocument()

  fresh = true
  fireEvent.click(screen.getByRole('button', { name: 'Reload latest' }))
  await waitFor(() => expect(pane().getByRole('combobox', { name: 'Status' })).toHaveValue('Resolved'))
  expect(screen.queryByText(/Someone else changed this ticket/)).not.toBeInTheDocument()
  expect(screen.getByRole('textbox', { name: 'Public reply' })).toHaveValue('my half written reply')
  expect(calls.filter((c) => c.method === 'GET' && c.path === '/api/tickets/IT-1').length).toBeGreaterThan(1)
})

test('invalid_state on a reply shows the server message and keeps the draft', async () => {
  mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/messages': () =>
      problem(409, 'Conflict', { code: 'invalid_state', detail: 'Reopen the ticket before replying.' }),
  })
  await openTicket()
  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'hello' } })
  fireEvent.click(screen.getByRole('button', { name: 'Post public reply' }))
  expect(await screen.findByText('Reopen the ticket before replying.')).toBeInTheDocument()
  expect(screen.getByRole('textbox', { name: 'Public reply' })).toHaveValue('hello')
})

test('closed ticket: public reply is unavailable, internal notes still are', async () => {
  mockFetch(ticketRoutes(agentMe(), makeDetail({ status: 'Closed' })))
  await openTicket()
  expect(screen.getByRole('radio', { name: 'Public reply' })).toBeDisabled()
  expect(screen.getByRole('radio', { name: 'Internal note' })).toBeChecked()
})

test('history: human sentences from action/previous/next, message events are not duplicated', async () => {
  mockFetch({
    ...ticketRoutes(),
    'GET /api/tickets/t1/activity': () =>
      page([
        { id: 'a3', occurredAt: '2026-10-01T12:00:00Z', actor: { id: 'u1', displayName: 'Ann Agent' }, action: 'ticket.status_changed', previous: { status: 'Open' }, next: { status: 'Pending' } },
        { id: 'a2', occurredAt: '2026-10-01T11:00:00Z', actor: { id: 'u1', displayName: 'Ann Agent' }, action: 'ticket.message_added', previous: null, next: { messageId: 'm2', kind: 'Public', authorType: 'Agent' } },
        { id: 'a1', occurredAt: '2026-10-01T09:00:00Z', actor: null, action: 'ticket.created', previous: null, next: { reference: 'IT-1', status: 'Open', priority: 'Normal' } },
      ]),
  })
  await openTicket()
  fireEvent.click(screen.getByRole('button', { name: 'History' }))
  expect(await screen.findByText(/changed status from Open to Pending/)).toBeInTheDocument()
  expect(screen.getByText('Ann Agent')).toBeInTheDocument()
  expect(screen.getByText(/created the ticket \(Open, Normal\)/)).toBeInTheDocument()
  expect(screen.queryByText(/message added/i)).not.toBeInTheDocument()
  expect(screen.getAllByRole('listitem').filter((li) => li.closest('.timeline'))).toHaveLength(2)
})

test('new ticket: requester email, subject, description and priority are posted; opens the new ticket', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'GET /api/departments/d1/requesters/lookup': () => [],
    'POST /api/departments/d1/tickets': () => json(makeDetail({ id: 't7', reference: 'IT-7', subject: 'Brand new' }), 201),
    'GET /api/tickets/IT-7': () => makeDetail({ id: 't7', reference: 'IT-7', subject: 'Brand new' }),
    'GET /api/tickets/t7/messages': () => page([]),
  })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: 'New ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'New ticket' })
  fireEvent.change(within(dialog).getByLabelText('Requester email or name'), { target: { value: 'new@example.test' } })
  fireEvent.change(within(dialog).getByLabelText('Subject'), { target: { value: 'Brand new' } })
  fireEvent.change(within(dialog).getByLabelText('Description'), { target: { value: 'It broke' } })
  fireEvent.change(within(dialog).getByLabelText('Priority'), { target: { value: 'High' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Create ticket' }))
  expect(await screen.findByRole('heading', { name: 'Brand new', level: 2 })).toBeInTheDocument()
  expect(calls.find((c) => c.method === 'POST')?.body).toEqual({
    subject: 'Brand new',
    description: 'It broke',
    requester: { email: 'new@example.test' },
    priority: 'High',
  })
})

test('internal note: typing @ offers eligible colleagues, picking one inserts the name and sends the mention id', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'GET /api/tickets/t1/mentionable?q=Le': () => [{ userId: 'u2', displayName: 'Lena Lead' }],
    'POST /api/tickets/t1/messages': () => json({ message: makeMessage({ id: 'm3', kind: 'Internal', authorType: 'Agent', author: { id: 'u1', displayName: 'Test User' }, body: 'x' }), ticket: makeDetail({ version: 'v3' }) }, 201),
  })
  await openTicket()
  fireEvent.click(screen.getByRole('radio', { name: /Internal note/ }))
  const box = screen.getByRole('textbox', { name: 'Internal note' })
  fireEvent.change(box, { target: { value: 'Please look @Le', selectionStart: 15 } })
  fireEvent.click(await screen.findByRole('button', { name: '@Lena Lead' }))
  expect((screen.getByRole('textbox', { name: 'Internal note' }) as HTMLTextAreaElement).value).toBe('Please look @Lena Lead ')
  fireEvent.click(screen.getByRole('button', { name: 'Add internal note' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/tickets/t1/messages')).toBe(true))
  const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/tickets/t1/messages')!.body as { kind: string; mentionUserIds?: string[] }
  expect(sent).toMatchObject({ kind: 'Internal', mentionUserIds: ['u2'] })
})

test('a retried post carries the same client message id, and the next message gets a new one', async () => {
  let attempt = 0
  const calls = mockFetch({
    ...ticketRoutes(),
    'POST /api/tickets/t1/messages': () => {
      attempt += 1
      return attempt === 1
        ? problem(500, 'Server error')
        : json({ message: makeMessage({ id: 'm9', kind: 'Public', authorType: 'Agent', author: { id: 'u1', displayName: 'Test User' }, body: 'x' }), ticket: makeDetail({ version: 'v3' }) }, 201)
    },
  })
  await openTicket()
  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'First answer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Post public reply' }))
  await waitFor(() => expect(attempt).toBe(1))
  fireEvent.click(await screen.findByRole('button', { name: 'Post public reply' }))
  await waitFor(() => expect(attempt).toBe(2))
  const ids = calls.filter((c) => c.method === 'POST' && c.path === '/api/tickets/t1/messages').map((c) => (c.body as { clientMessageId?: string }).clientMessageId)
  expect(ids[0]).toBeTruthy()
  expect(ids[1]).toBe(ids[0]) // the retry is the same message to the server

  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'Second answer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Post public reply' }))
  await waitFor(() => expect(attempt).toBe(3))
  const third = calls.filter((c) => c.method === 'POST' && c.path === '/api/tickets/t1/messages')[2].body as { clientMessageId?: string }
  expect(third.clientMessageId).not.toBe(ids[0])
})

test('a saved internal note marks the mentioned colleague, public replies never do', async () => {
  mockFetch({
    ...ticketRoutes(),
    'GET /api/tickets/t1/messages': () =>
      page([
        makeMessage({ id: 'n1', kind: 'Internal', authorType: 'Agent', author: { id: 'u1', displayName: 'Test User' }, body: 'Over to @Lena Lead please', mentions: [{ userId: 'u2', displayName: 'Lena Lead' }] }),
      ]),
  })
  await openTicket()
  const mark = await screen.findByText('@Lena Lead')
  expect(mark.tagName).toBe('MARK')
})

test('presence: others on the ticket are listed quietly, the draft survives new activity with a non-blocking warning', async () => {
  const detailAt = (updatedAt: string) => makeDetail({ id: 't1', reference: 'IT-1', subject: 'Printer jam', updatedAt })
  let current = detailAt('2026-01-12T09:00:00Z')
  mockFetch({ ...ticketRoutes(), 'GET /api/tickets/IT-1': () => current })
  await openTicket()
  act(() => applyPresence({ ticketId: 't1', users: [{ userId: 'u9', displayName: 'Sarah', state: 'viewing' }, { userId: 'u8', displayName: 'Bob', state: 'reply' }, { userId: 'u7', displayName: 'Sue', state: 'note' }] }))
  expect(await screen.findByText('Sarah is viewing this ticket · Bob is writing a reply · Sue is writing an internal note')).toBeInTheDocument()

  // Writing a public reply, then somebody else changes the ticket: a warning appears and the draft stays.
  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'Working on it' } })
  expect(screen.queryByText(/new activity since you started writing/)).not.toBeInTheDocument()
  current = detailAt('2026-01-12T09:05:00Z')
  window.dispatchEvent(new Event('visibilitychange'))
  expect(await screen.findByText(/new activity since you started writing/)).toBeInTheDocument()
  expect((screen.getByRole('textbox', { name: 'Public reply' }) as HTMLTextAreaElement).value).toBe('Working on it')
  expect(screen.getByRole('button', { name: 'Post public reply' })).toBeEnabled() // never blocks sending
})

test('new ticket: typing the start of a name finds an earlier requester and picking it links the ticket to them', async () => {
  mockFetch({
    ...workspaceRoutes(agentMe()),
    'GET /api/departments/d1/requesters/lookup?q=dan': () => json([{ id: 'r1', displayName: 'Dana Whitfield', email: 'dana.whitfield@contoso.test' }]),
  })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: /New ticket/ }))
  const dialog = await screen.findByRole('dialog')
  fireEvent.change(within(dialog).getByLabelText(/Requester email or name/), { target: { value: 'dan' } })
  fireEvent.click(await within(dialog).findByRole('button', { name: /Dana Whitfield/ }))
  expect(await within(dialog).findByText(/dana.whitfield@contoso.test/)).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Change requester' })).toBeInTheDocument()
})

test('new ticket: the ticket type can be chosen (default preselected) and is posted', async () => {
  const calls = mockFetch({
    ...ticketRoutes(),
    'GET /api/departments/d1/requesters/lookup': () => [],
    'GET /api/departments/d1/ticket-types/options': () => [
      { id: 'ty1', name: 'Incident', isActive: true, isDefault: true },
      { id: 'ty2', name: 'Service request', isActive: true, isDefault: false },
    ],
    'POST /api/departments/d1/tickets': () => json(makeDetail({ id: 't7', reference: 'IT-7', subject: 'Brand new' }), 201),
    'GET /api/tickets/IT-7': () => makeDetail({ id: 't7', reference: 'IT-7', subject: 'Brand new' }),
    'GET /api/tickets/t7/messages': () => page([]),
  })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: 'New ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'New ticket' })
  const select = (await within(dialog).findByLabelText('Ticket type')) as HTMLSelectElement
  expect(select.value).toBe('ty1') // the department default
  fireEvent.change(select, { target: { value: 'ty2' } })
  fireEvent.change(within(dialog).getByLabelText('Requester email or name'), { target: { value: 'new@example.test' } })
  fireEvent.change(within(dialog).getByLabelText('Subject'), { target: { value: 'Brand new' } })
  fireEvent.change(within(dialog).getByLabelText('Description'), { target: { value: 'It broke' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Create ticket' }))
  expect(await screen.findByRole('heading', { name: 'Brand new', level: 2 })).toBeInTheDocument()
  expect((calls.find((c) => c.method === 'POST' && c.path === '/api/departments/d1/tickets')?.body as { ticketTypeId?: string }).ticketTypeId).toBe('ty2')
})

test('the workspace sidebar collapses to icons, expands again and remembers the choice', async () => {
  window.localStorage.removeItem('helpdesk.nav.collapsed')
  mockFetch({ ...workspaceRoutes(agentMe()) })
  renderApp('/queue/my')
  const toggle = await screen.findByRole('button', { name: 'Collapse sidebar' })
  expect(toggle).toHaveAttribute('aria-expanded', 'true')
  fireEvent.click(toggle)
  const expand = await screen.findByRole('button', { name: 'Expand sidebar' })
  expect(expand).toHaveAttribute('aria-expanded', 'false')
  expect(document.querySelector('.shell')).toHaveClass('nav-collapsed')
  expect(window.localStorage.getItem('helpdesk.nav.collapsed')).toBe('1')
  expect(screen.getByRole('link', { name: /My Queue/ })).toHaveAttribute('title', 'My Queue') // still reachable and named when only the icon shows
  fireEvent.click(expand)
  expect(await screen.findByRole('button', { name: 'Collapse sidebar' })).toBeInTheDocument()
  expect(window.localStorage.getItem('helpdesk.nav.collapsed')).toBe('0')
})

test('the navigation strip can be hidden from the top bar and shown again, and the choice is remembered', async () => {
  window.localStorage.removeItem('helpdesk.rail.hidden')
  mockFetch({ ...workspaceRoutes(agentMe()) })
  renderApp('/queue/my')
  const hide = await screen.findByRole('button', { name: 'Hide navigation' })
  expect(hide).toHaveAttribute('aria-expanded', 'true')
  fireEvent.click(hide)
  const show = await screen.findByRole('button', { name: 'Show navigation' })
  expect(show).toHaveAttribute('aria-expanded', 'false')
  expect(document.querySelector('.shell')).toHaveClass('rail-hidden')
  expect(window.localStorage.getItem('helpdesk.rail.hidden')).toBe('1')
  fireEvent.click(show)
  expect(await screen.findByRole('button', { name: 'Hide navigation' })).toBeInTheDocument()
  expect(document.querySelector('.shell')).not.toHaveClass('rail-hidden')
})
