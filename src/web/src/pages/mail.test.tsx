import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { describeActivity } from '../lib/activity'
import {
  AGENT_PERMS,
  agentMe,
  csrfHandler,
  DEPT,
  makeDetail,
  makeMe,
  makeMessage,
  membership,
  mockFetch,
  page,
  problem,
  renderApp,
  workspaceRoutes,
} from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const AGENT_REPLY = { authorType: 'Agent' as const, author: { id: 'u1', displayName: 'Ann Agent' }, body: 'We are on it' }

const ticketRoutes = (messages = [makeMessage()], detail = makeDetail({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })) => ({
  ...workspaceRoutes(agentMe()),
  'GET /api/tickets/IT-1': () => detail,
  'GET /api/tickets/t1/messages': () => page(messages),
  'GET /api/tickets/t1/activity': () => page([]),
})

const openTicket = async () => {
  renderApp('/queue/my/IT-1')
  await screen.findByRole('heading', { name: 'Printer jam', level: 2 })
}

test('delivery states are reported truthfully: queued, sent, failed, and recorded only', async () => {
  mockFetch(
    ticketRoutes([
      makeMessage({ id: 'a', ...AGENT_REPLY, body: 'queued one', deliveryState: 'Pending' }),
      makeMessage({ id: 'b', ...AGENT_REPLY, body: 'sent one', deliveryState: 'Sent' }),
      makeMessage({ id: 'c', ...AGENT_REPLY, body: 'failed one', deliveryState: 'Failed', deliveryError: 'send_rejected' }),
      makeMessage({ id: 'd', ...AGENT_REPLY, body: 'unsent one', deliveryState: 'NotDelivered' }),
    ]),
  )
  await openTicket()
  expect(await screen.findByText('Queued to send by email')).toBeInTheDocument()
  expect(screen.getByText('Sent by email from the ticket’s mailbox (accepted for delivery; not confirmed as delivered)')).toBeInTheDocument()
  expect(screen.getByText(/Email delivery failed \(send_rejected\)/)).toBeInTheDocument()
  expect(screen.getByText(/Recorded, not emailed/)).toBeInTheDocument()
  // exactly one entry claims a send
  expect(screen.getAllByText(/Sent by email/)).toHaveLength(1)
})

test('a failed delivery can be retried and the request goes to the retry endpoint', async () => {
  const calls = mockFetch({
    ...ticketRoutes([makeMessage({ id: 'c', ...AGENT_REPLY, deliveryState: 'Failed', deliveryError: 'send_rejected' })]),
    'POST /api/tickets/t1/messages/c/retry-delivery': () => makeMessage({ id: 'c', ...AGENT_REPLY, deliveryState: 'Pending' }),
  })
  await openTicket()
  fireEvent.click(await screen.findByRole('button', { name: 'Retry sending' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/messages/c/retry-delivery'))).toBe(true))
})

test('attachments: downloadable files link to the download endpoint, blocked ones are not links', async () => {
  mockFetch(
    ticketRoutes([
      makeMessage({
        attachments: [
          { id: 'f1', fileName: 'report.pdf', contentType: 'application/pdf', size: 2048, isInline: false, state: 'Stored', blockReason: null, scanState: 'Clean', downloadable: true },
          { id: 'f2', fileName: 'setup.exe', contentType: 'application/octet-stream', size: 10, isInline: false, state: 'Blocked', blockReason: 'blocked_type', scanState: 'NotScanned', downloadable: false },
          { id: 'f3', fileName: 'wait.txt', contentType: 'text/plain', size: 5, isInline: false, state: 'Stored', blockReason: null, scanState: 'Skipped', downloadable: false },
        ],
      }),
    ]),
  )
  await openTicket()
  const link = await screen.findByRole('link', { name: 'report.pdf' })
  expect(link).toHaveAttribute('href', '/api/tickets/t1/attachments/f1')
  expect(link).toHaveAttribute('download')
  expect(screen.queryByRole('link', { name: 'setup.exe' })).not.toBeInTheDocument()
  expect(screen.getByText(/not available: blocked file type/)).toBeInTheDocument()
  expect(screen.getByText(/awaiting malware scan/)).toBeInTheDocument()
})

test('formatted email view is a sandboxed frame without script rights and is opt-in; notes have none', async () => {
  mockFetch(
    ticketRoutes([
      makeMessage({ id: 'h', body: 'plain version', bodyHtml: '<p>Rich <b>text</b></p>' }),
      makeMessage({ id: 'n', kind: 'Internal', authorType: 'Agent', author: { id: 'u1', displayName: 'Ann Agent' }, body: 'internal', bodyHtml: '<p>nope</p>' }),
    ]),
  )
  await openTicket()
  expect(await screen.findByText('plain version')).toBeInTheDocument()
  expect(screen.queryByTitle('Formatted email')).not.toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Show formatted' })).toHaveLength(1)

  fireEvent.click(screen.getByRole('button', { name: 'Show formatted' }))
  const frame = await screen.findByTitle('Formatted email')
  expect(frame).toHaveAttribute('sandbox', 'allow-popups')
  expect(frame.getAttribute('sandbox')).not.toContain('allow-scripts')
  expect(frame.getAttribute('sandbox')).not.toContain('allow-same-origin')
  const doc = frame.getAttribute('srcdoc') ?? ''
  expect(doc).toContain("default-src 'none'")
  expect(doc).toContain('<p>Rich <b>text</b></p>')
  fireEvent.click(screen.getByRole('button', { name: 'Show plain text' }))
  expect(screen.queryByTitle('Formatted email')).not.toBeInTheDocument()
})

test('the ticket header shows the mailbox it arrived through and the people in copy', async () => {
  mockFetch(
    ticketRoutes(
      [makeMessage()],
      makeDetail({
        id: 't1',
        reference: 'IT-1',
        subject: 'Printer jam',
        mailbox: { id: 'mb1', address: 'desk@helpdesk.test', name: 'IT Desk' },
        participants: [{ email: 'boss@client.test', displayName: 'The Boss' }, { email: 'copy@client.test' }],
      }),
    ),
  )
  await openTicket()
  const header = within(screen.getByRole('region', { name: 'Ticket' }))
  expect(await header.findByText('desk@helpdesk.test')).toBeInTheDocument()
  expect(header.getByText('The Boss, copy@client.test')).toBeInTheDocument()
})

test('activity: email creation and delivery changes read as sentences', () => {
  const names = { users: new Map<string, string>(), teams: new Map<string, string>() }
  const base = { id: 'a', occurredAt: '2026-10-01T10:00:00Z', actor: null, previous: null }
  expect(describeActivity({ ...base, action: 'ticket.created', next: { status: 'Open', priority: 'Normal', source: 'email' } }, names)).toBe(
    'created the ticket from an email (Open, Normal)',
  )
  expect(describeActivity({ ...base, action: 'ticket.message_delivery_changed', next: { delivery: 'Sent' } }, names)).toBe('sent a reply by email')
  expect(describeActivity({ ...base, action: 'ticket.message_delivery_changed', next: { delivery: 'Failed' } }, names)).toBe('could not send a reply by email')
})

// ----- administration -----

const adminMe = makeMe({ memberships: [membership([...AGENT_PERMS, 'department.manage', 'department.mailboxes.manage'], 'DepartmentAdmin')] })
const MAILBOX = {
  id: 'mb1',
  departmentId: 'd1',
  address: 'desk@helpdesk.test',
  displayName: 'IT Desk',
  provider: 'graph',
  isActive: true,
  isDefault: true,
  signature: 'Regards',
  version: 'v1',
  subscription: { state: 'Active', expiresAt: '2026-10-08T10:00:00Z' },
  lastSyncAt: '2026-10-05T10:00:00Z',
  lastSyncError: null,
  attentionCount: 1,
}
const platformMe = makeMe({ isPlatformAdmin: true, memberships: adminMe.memberships })
const deptRoutes = (me = adminMe) => ({
  'GET /api/me': () => me,
  'GET /api/departments/d1': () => DEPT,
  'GET /api/departments/d1/teams': () => [],
  'GET /api/departments/d1/members': () => [],
  'GET /api/departments/d1/mailboxes': () => [MAILBOX],
  'GET /api/departments/d1/auto-reply': () => ({ departmentId: 'd1', configured: false, enabled: false, forEmail: true, forPortal: false, subject: 'Subject {{ticket.reference}}', body: 'Body', placeholders: ['ticket.reference'], version: '' }),
  'GET /api/departments/d1/mail/attention': () =>
    page([
      { id: 'x1', mailboxId: 'mb1', direction: 'Inbound', state: 'Failed', ignoreReason: null, occurredAt: '2026-10-05T09:00:00Z', address: 'a@client.test', subject: 'Broken mail', lastError: 'TransientProviderException', attempts: 5, ticketId: null },
      { id: 'x2', mailboxId: 'mb1', direction: 'Inbound', state: 'Ignored', ignoreReason: 'auto_submitted', occurredAt: '2026-10-05T08:00:00Z', address: 'b@client.test', subject: 'Out of office', lastError: null, attempts: 0, ticketId: null },
    ]),
  ...csrfHandler,
})

test('mailbox administration: shows health and mail needing attention, and failed mail can be retried', async () => {
  const calls = mockFetch({ ...deptRoutes(), 'POST /api/departments/d1/mail/x1/retry': () => new Response(null, { status: 204 }) })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Mailboxes' }))
  expect(await screen.findByRole('heading', { name: 'Mailboxes' })).toBeInTheDocument()
  expect(await screen.findByText('desk@helpdesk.test')).toBeInTheDocument()
  expect(screen.getByText('Default')).toBeInTheDocument()
  expect(screen.getByText(/1 failed/)).toBeInTheDocument()
  expect(await screen.findByText('Ignored: automatic reply')).toBeInTheDocument()
  expect(screen.getByText(/Failed \(TransientProviderException\) after 5 attempt/)).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(1) // only failed mail is retryable
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path.endsWith('/mail/x1/retry'))).toBe(true))
})

test('mailbox administration is not offered without department.mailboxes.manage', async () => {
  mockFetch(deptRoutes(agentMe()))
  renderApp('/admin/departments/d1')
  await screen.findByRole('tab', { name: 'Members' })
  expect(screen.queryByRole('tab', { name: 'Mailboxes' })).not.toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Mailboxes' })).not.toBeInTheDocument()
  expect(screen.queryByRole('form', { name: 'New mailbox' })).not.toBeInTheDocument()
})

test('only a platform admin is offered Add mailbox', async () => {
  mockFetch(deptRoutes())
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Mailboxes' }))
  await screen.findByRole('heading', { name: 'Mailboxes' })
  expect(screen.queryByRole('button', { name: 'Add mailbox' })).not.toBeInTheDocument()
})

test('creating a mailbox posts the trimmed values and shows server field errors', async () => {
  let attempt = 0
  const calls = mockFetch({
    ...deptRoutes(platformMe),
    'POST /api/departments/d1/mailboxes': () => {
      attempt += 1
      return attempt === 1
        ? new Response(JSON.stringify({ status: 409, title: 'Conflict', code: 'duplicate', detail: 'A mailbox with this address already exists.' }), { status: 409, headers: { 'content-type': 'application/problem+json' } })
        : new Response(JSON.stringify({ ...MAILBOX, id: 'mb2', address: 'new@helpdesk.test' }), { status: 201, headers: { 'content-type': 'application/json' } })
    },
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Mailboxes' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Add mailbox' }))
  const form = await screen.findByRole('form', { name: 'New mailbox' })
  fireEvent.change(within(form).getByLabelText('Mailbox address'), { target: { value: '  new@helpdesk.test ' } })
  fireEvent.change(within(form).getByLabelText('Sender name'), { target: { value: ' IT Desk ' } })
  fireEvent.click(within(form).getByRole('button', { name: 'Add mailbox' }))
  expect(await screen.findByText(/already exists/)).toBeInTheDocument()
  fireEvent.click(within(form).getByRole('button', { name: 'Add mailbox' }))
  await waitFor(() => expect(calls.filter((c) => c.method === 'POST' && c.path.endsWith('/mailboxes'))).toHaveLength(2))
  expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ address: 'new@helpdesk.test', displayName: 'IT Desk' })
})

test('mailbox health shows its status and reasons, and an uncertain send is resolved only after a warning', async () => {
  const calls = mockFetch({
    ...deptRoutes(),
    'GET /api/departments/d1/mailboxes': () => [
      { ...MAILBOX, health: { status: 'Attention', reasons: ['subscription_expiring', 'outbound_uncertain'] }, lastSendAt: '2026-10-05T09:30:00Z', outboundUncertainCount: 1, outboundFailedCount: 0 },
    ],
    'GET /api/departments/d1/mail/attention': () =>
      page([
        { id: 'u1', mailboxId: 'mb1', direction: 'Outbound', state: 'Uncertain', ignoreReason: null, occurredAt: '2026-10-05T09:00:00Z', address: 'desk@helpdesk.test', subject: 'Re: Printer', lastError: 'outcome_uncertain', attempts: 1, ticketId: 't1', category: 'outcome_uncertain' },
      ]),
    'POST /api/departments/d1/mail/u1/retry': (c) =>
      (c.body as { acceptDuplicateRisk?: boolean } | undefined)?.acceptDuplicateRisk ? new Response(JSON.stringify({ outcome: 'requeued' }), { status: 200, headers: { 'Content-Type': 'application/json' } }) : problem(409, 'Conflict', { code: 'outcome_uncertain', detail: 'x' }),
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Mailboxes' }))
  expect(await screen.findByText('Attention')).toBeInTheDocument()
  expect(screen.getByText(/Inbound subscription renewal is overdue · Some sends have an uncertain outcome/)).toBeInTheDocument()
  expect(await screen.findByText('Send outcome uncertain (not retried automatically)')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument() // no plain retry for an uncertain send
  fireEvent.click(screen.getByRole('button', { name: 'Resolve…' }))
  const dialog = await screen.findByRole('dialog', { name: 'Send outcome uncertain' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Check with the mail service' }))
  expect(await within(dialog).findByText(/requester receives it twice/)).toBeInTheDocument()
  expect(calls.filter((c) => c.method === 'POST').every((c) => !(c.body as { acceptDuplicateRisk?: boolean } | undefined)?.acceptDuplicateRisk)).toBe(true)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Re-send anyway (may duplicate)' }))
  await waitFor(() => expect(calls.some((c) => (c.body as { acceptDuplicateRisk?: boolean } | undefined)?.acceptDuplicateRisk === true)).toBe(true))
})

test('a failed send shows a safe category; an uncertain one offers no retry button', async () => {
  mockFetch(ticketRoutes([makeMessage({ id: 'c', ...AGENT_REPLY, body: 'maybe sent', deliveryState: 'Uncertain', deliveryError: 'outcome_uncertain' })]))
  await openTicket()
  expect(await screen.findByText(/Send outcome uncertain: it may or may not have been sent/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Retry sending' })).not.toBeInTheDocument()
})
