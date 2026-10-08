import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { makeDetail, makeMessage, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const CANDIDATE = { id: 't9', reference: 'IT-1047', subject: 'VPN disconnecting', status: 'Open', requesterName: 'Vic', requesterEmail: 'vic@example.test', version: 'v9' }

const routes = (detail = makeDetail()) => ({
  ...workspaceRoutes(),
  'GET /api/tickets/IT-1': () => detail,
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
})

const PARTICIPANTS = [
  { email: 'colleague@example.test', displayName: 'Colin Colleague', isProtected: false },
  { email: 'vic@example.test', displayName: 'Vic Merged', isProtected: true },
]

test('the public reply lists who it is copied to by name and address; an ordinary participant can be removed, a merged requester cannot', async () => {
  const calls = mockFetch({
    ...routes(makeDetail({ participants: PARTICIPANTS })),
    'POST /api/tickets/t1/participants/remove': () => [PARTICIPANTS[1]],
  })
  renderApp('/queue/my/IT-1')
  const cc = await screen.findByLabelText('Copied on this reply')
  expect(cc).toHaveTextContent('Colin Colleague <colleague@example.test>')
  expect(cc).toHaveTextContent('Vic Merged <vic@example.test>')
  expect(cc).toHaveTextContent('requester of a merged ticket, always copied')
  expect(within(cc).getAllByRole('button')).toHaveLength(1) // only the ordinary participant has a remove control

  fireEvent.click(within(cc).getByRole('button', { name: 'Stop copying colleague@example.test on replies' }))
  await vi.waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/tickets/t1/participants/remove')).toBe(true))
  expect(calls.find((c) => c.path === '/api/tickets/t1/participants/remove')!.body).toEqual({ email: 'colleague@example.test' })
})

test('merging tickets of different requesters warns that the other requester joins the conversation; the same requester does not', async () => {
  mockFetch({
    ...routes(),
    'GET /api/tickets/t1/link-candidates?q=VPN': () => [CANDIDATE, { ...CANDIDATE, id: 't8', reference: 'IT-1048', subject: 'VPN again', requesterName: 'Rita Requester', requesterEmail: 'RITA@example.test' }],
  })
  renderApp('/queue/my/IT-1')
  const pane = await screen.findByRole('region', { name: 'Ticket' })
  fireEvent.click(await within(pane).findByRole('button', { name: 'More' }))
  fireEvent.click(screen.getByRole('button', { name: 'Merge ticket' }))
  const dialog = await screen.findByRole('dialog', { name: 'Merge ticket' })
  fireEvent.change(within(dialog).getByLabelText('Find a ticket by reference or subject'), { target: { value: 'VPN' } })

  fireEvent.click(await within(dialog).findByRole('radio', { name: 'IT-1047 VPN disconnecting' }))
  const warning = within(dialog).getByRole('note')
  expect(warning).toHaveTextContent('Another requester will join the conversation')
  expect(warning).toHaveTextContent('Rita Requester <rita@example.test> will be added to IT-1047 as a protected participant')
  expect(warning).toHaveTextContent('copied on every future public reply')

  fireEvent.click(await within(dialog).findByRole('radio', { name: 'IT-1048 VPN again' }))
  expect(within(dialog).queryByRole('note')).not.toBeInTheDocument() // same requester (address compared case-insensitively)
})
