import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, AGENT_PERMS, csrfHandler, DEPT, makeDetail, makeMe, makeMessage, membership, mockFetch, page, renderApp, viewerMe, workspaceRoutes, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const twoDepartments = makeMe({
  displayName: 'Anton Bodell',
  email: 'anton@example.test',
  memberships: [
    { ...membership(AGENT_PERMS), departmentId: 'd1', departmentName: 'IT Support' },
    { ...membership(AGENT_PERMS), departmentId: 'd2', departmentKey: 'HR', departmentName: 'HR' },
  ],
})

const sig = (departmentId: string, name: string, html: string | null) => ({ departmentId, departmentName: name, html, updatedAt: null, placeholders: ['agent.name', 'agent.email', 'department.name', 'mailbox.displayName', 'mailbox.email'] })

test('My profile shows the read-only identity and a per-department signature that is edited, previewed and saved', async () => {
  const calls: Call[] = []
  mockFetch({
    'GET /api/me': () => twoDepartments,
    'GET /api/departments/d1/signature/me': () => sig('d1', 'IT Support', '<p>Anton<br>IT Support Engineer</p>'),
    'GET /api/departments/d2/signature/me': () => sig('d2', 'HR', null),
    'POST /api/departments/d1/signature/me/preview': (c: Call) => {
      calls.push(c)
      return { html: '<p>Anton Bodell</p>' }
    },
    'PUT /api/departments/d1/signature/me': (c: Call) => {
      calls.push(c)
      return sig('d1', 'IT Support', '<p>Anton Bodell</p>')
    },
    ...csrfHandler,
  })
  renderApp('/profile')
  expect(await screen.findByRole('heading', { name: 'My profile', level: 1 })).toBeInTheDocument()
  expect(screen.getByText('anton@example.test')).toBeInTheDocument()
  expect(screen.getByText(/never from your own address/)).toBeInTheDocument()

  const editor = await screen.findByRole('textbox', { name: 'Signature' })
  await waitFor(() => expect(editor.innerHTML).toContain('IT Support Engineer'))
  editor.innerHTML = '<p>Anton Bodell</p>'
  fireEvent.input(editor)
  fireEvent.click(screen.getByRole('button', { name: 'Preview' }))
  const preview = await screen.findByRole('region', { name: 'Signature preview' })
  expect(within(preview).getByTitle('Signature preview')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Save signature' }))
  await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
  expect(calls.find((c) => c.method === 'PUT')!.path).toBe('/api/departments/d1/signature/me')
  expect(calls.find((c) => c.method === 'PUT')!.body).toEqual({ html: '<p>Anton Bodell</p>' })

  // Another department has its own (empty) signature.
  fireEvent.change(screen.getByLabelText('Department'), { target: { value: 'd2' } })
  await waitFor(() => expect((screen.getByRole('textbox', { name: 'Signature' }) as HTMLElement).innerHTML).toBe(''))
  expect(screen.getByText(/Signature for/)).toHaveTextContent('HR')
})

test('someone who cannot reply to requesters has no signature to edit', async () => {
  mockFetch({ 'GET /api/me': () => viewerMe(), ...csrfHandler })
  renderApp('/profile')
  expect(await screen.findByText(/do not belong to a department where you can reply/)).toBeInTheDocument()
  expect(screen.queryByRole('textbox', { name: 'Signature' })).not.toBeInTheDocument()
})

const adminMe = makeMe({ memberships: [membership([...AGENT_PERMS, 'department.manage', 'department.mailboxes.manage'], 'DepartmentAdmin')] })

test('a department configures its own acknowledgement: toggles, templates, preview, save', async () => {
  const calls: Call[] = []
  mockFetch({
    'GET /api/me': () => adminMe,
    'GET /api/departments/d1': () => DEPT,
    'GET /api/departments/d1/teams': () => [],
    'GET /api/departments/d1/members': () => [],
    'GET /api/departments/d1/mailboxes': () => [],
    'GET /api/departments/d1/mail/attention': () => page([]),
    'GET /api/departments/d1/auto-reply': () => ({
      departmentId: 'd1', configured: false, enabled: false, forEmail: true, forPortal: false,
      subject: 'We have received your request — {{ticket.reference}}', body: 'Hi {{requester.name}},', placeholders: ['requester.name', 'ticket.reference'], version: '',
    }),
    'POST /api/departments/d1/auto-reply/preview': () => ({ subject: 'We have received your request — IT-1042', body: 'Hi Jane Smith,', fromName: 'IT Helpdesk', fromAddress: 'itsupport@constco.com' }),
    'PUT /api/departments/d1/auto-reply': (c: Call) => {
      calls.push(c)
      return { departmentId: 'd1', configured: true, enabled: true, forEmail: true, forPortal: true, subject: 'S', body: 'B', placeholders: [], version: 'v1' }
    },
    ...csrfHandler,
  })
  renderApp('/admin/departments/d1')
  fireEvent.click(await screen.findByRole('tab', { name: 'Mailboxes' }))
  const form = await screen.findByRole('form', { name: 'Automatic acknowledgement' })
  expect(within(form).getByLabelText('Enabled')).not.toBeChecked() // off until the department turns it on
  expect(within(form).getByLabelText('Email-created tickets')).toBeChecked()
  expect(within(form).getByLabelText('Portal-created tickets')).not.toBeChecked()
  expect(screen.getByText(/never includes an agent signature/)).toBeInTheDocument()

  fireEvent.click(within(form).getByLabelText('Enabled'))
  fireEvent.click(within(form).getByLabelText('Portal-created tickets'))
  fireEvent.change(within(form).getByLabelText('Subject'), { target: { value: 'Got it {{ticket.reference}}' } })
  fireEvent.click(within(form).getByRole('button', { name: 'Preview' }))
  const preview = await screen.findByRole('region', { name: 'Acknowledgement preview' })
  expect(preview).toHaveTextContent('IT Helpdesk <itsupport@constco.com>')
  expect(preview).toHaveTextContent('Hi Jane Smith,')
  fireEvent.click(within(form).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(calls.length).toBe(1))
  expect(calls[0].body).toEqual({ enabled: true, forEmail: true, forPortal: true, subject: 'Got it {{ticket.reference}}', body: 'Hi {{requester.name}},' })
})

test('a ticket created by hand offers the mailbox choice only when the department has several, and sends the chosen one', async () => {
  const calls = mockFetch({
    ...workspaceRoutes(agentMe()),
    'GET /api/departments/d1/outbound-mailboxes': () => [
      { id: 'mb1', address: 'itsupport@constco.com', name: 'IT Helpdesk', isDefault: true },
      { id: 'mb2', address: 'network@constco.com', name: 'Network Support', isDefault: false },
    ],
    'POST /api/departments/d1/tickets': () => makeDetail(),
  })
  renderApp('/queue/my')
  fireEvent.click(await screen.findByRole('button', { name: 'New ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'New ticket' })
  const select = await within(dialog).findByLabelText('Mailbox')
  expect(within(select).getByRole('option', { name: /Default \(IT Helpdesk\)/ })).toBeInTheDocument()
  fireEvent.change(select, { target: { value: 'mb2' } })
  fireEvent.change(within(dialog).getByLabelText('Requester email or name'), { target: { value: 'jane@example.test' } })
  fireEvent.change(within(dialog).getByLabelText('Subject'), { target: { value: 'Printer' } })
  fireEvent.change(within(dialog).getByLabelText('Description'), { target: { value: 'It jams' } })
  fireEvent.click(within(dialog).getByRole('button', { name: /Create ticket/ }))
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/departments/d1/tickets')).toBe(true))
  expect(calls.find((c) => c.path === '/api/departments/d1/tickets')!.body).toMatchObject({ mailboxId: 'mb2', subject: 'Printer' })
})

test('the reply box says whether the sending agent has a signature for the ticket department', async () => {
  mockFetch({
    ...workspaceRoutes(agentMe()),
    'GET /api/tickets/IT-1': () => makeDetail(),
    'GET /api/tickets/t1/messages': () => page([makeMessage()]),
    'GET /api/tickets/t1/activity': () => page([]),
    'GET /api/departments/d1/signature/me': () => sig('d1', 'IT Support', '<p>Anton</p>'),
  })
  renderApp('/queue/my/IT-1')
  expect(await screen.findByText(/Your IT Support signature is added when you send/)).toBeInTheDocument()
  fireEvent.click(screen.getByLabelText(/Internal note/))
  expect(screen.queryByText(/signature is added when you send/)).not.toBeInTheDocument() // notes never get one
})
