import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { csrfHandler, makeMe, mockFetch, problem, renderApp } from '../../test-utils'

afterEach(() => vi.unstubAllGlobals())

const message = { id: 'm1', author: 'Support', fromYou: false, body: 'Please describe the problem.', at: '2026-10-01T09:00:00Z', attachments: [] }
const request = { reference: 'IT-1', subject: 'Printer issue', status: 'Open', statusLabel: 'In progress', department: 'IT', createdAt: message.at, lastActivityAt: message.at, fields: [], messages: [message] }
const routes = {
  ...csrfHandler,
  'GET /api/me': () => makeMe(),
  'GET /api/portal/forms/f1': () => ({ id: 'f1', name: 'Hardware issue', department: 'IT', fields: [] }),
  'GET /api/portal/requests/IT-1': () => request,
  'GET /api/portal/knowledge/articles': () => ({ items: [], nextCursor: null }),
}

test.each(['/portal/submit/f1', '/portal/requests/IT-1'])('attachment controls follow the real capability on %s', async (route) => {
  mockFetch({ ...routes, 'GET /api/portal/capabilities': () => ({ attachmentsEnabled: true }) })
  renderApp(route)
  expect(await screen.findByLabelText('Attach files (optional)')).toHaveAttribute('type', 'file')
})

test.each([
  ['/portal/submit/f1', false], ['/portal/requests/IT-1', false],
  ['/portal/submit/f1', 'error'], ['/portal/requests/IT-1', 'error'],
] as const)('attachment controls are hidden on %s when capability is %s', async (route, state) => {
  const calls = mockFetch({ ...routes, 'GET /api/portal/capabilities': () => state === 'error' ? problem(503, 'Unavailable') : { attachmentsEnabled: false } })
  renderApp(route)
  await screen.findByRole('heading', { level: 1, name: route.includes('submit') ? 'Hardware issue' : 'Printer issue' })
  await waitFor(() => expect(calls.some((c) => c.path === '/api/portal/capabilities')).toBe(true))
  expect(screen.queryByLabelText('Attach files (optional)')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: route.includes('submit') ? 'Submit request' : 'Send reply' })).toBeInTheDocument()
})

test('a disabled attachment pipeline still allows an ordinary requester reply', async () => {
  const calls = mockFetch({
    ...routes,
    'GET /api/portal/capabilities': () => ({ attachmentsEnabled: false }),
    'POST /api/portal/requests/IT-1/replies': () => ({ message, status: 'Open', statusLabel: 'In progress', attachmentsNotSaved: 0 }),
  })
  renderApp('/portal/requests/IT-1')
  fireEvent.change(await screen.findByLabelText('Add a reply'), { target: { value: 'The printer has a paper jam.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Send reply' }))
  await screen.findByText('Your reply was sent.')
  expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ body: 'The printer has a paper jam.', clientMessageId: expect.any(String) })
})
