import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { clearDrafts, draftCount } from '../lib/drafts'
import { makeDetail, makeMessage, makeTicket, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => {
  vi.unstubAllGlobals()
  clearDrafts()
})
beforeEach(() => window.sessionStorage.clear())

const T1 = makeTicket({ id: 't1', reference: 'IT-1', subject: 'Printer jam' })
const T2 = makeTicket({ id: 't2', reference: 'IT-2', subject: 'VPN down' })

const routes = () => ({
  ...workspaceRoutes(),
  'GET /api/tickets': () => page([T1, T2]),
  'GET /api/tickets/IT-1': () => makeDetail({ id: 't1', reference: 'IT-1', subject: 'Printer jam' }),
  'GET /api/tickets/IT-2': () => makeDetail({ id: 't2', reference: 'IT-2', subject: 'VPN down' }),
  'GET /api/tickets/t1/messages': () => page([]),
  'GET /api/tickets/t2/messages': () => page([makeMessage({ id: 'm1', ticketId: 't2', body: Array.from({ length: 40 }, (_, i) => `Line ${i + 1} of a long email`).join('\n') })]),
})

test('an unsent reply survives stepping to the next ticket and back, and shows as a draft in the list', async () => {
  mockFetch(routes())
  renderApp('/queue/all')
  fireEvent.click(await screen.findByRole('link', { name: 'Printer jam' }))
  const box = await screen.findByRole('textbox', { name: /Public reply|Internal note/ })
  fireEvent.change(box, { target: { value: 'Half-written answer' } })
  expect(draftCount()).toBe(1)

  fireEvent.click(await screen.findByRole('link', { name: /Next ticket, IT-2/ }))
  expect(await screen.findByRole('heading', { name: 'VPN down', level: 2 })).toBeInTheDocument()
  expect(screen.getByRole('textbox', { name: /Public reply|Internal note/ })).toHaveValue('')

  fireEvent.click(screen.getByRole('link', { name: /Previous ticket, IT-1/ }))
  expect(await screen.findByRole('heading', { name: 'Printer jam', level: 2 })).toBeInTheDocument()
  expect(screen.getByRole('textbox', { name: /Public reply|Internal note/ })).toHaveValue('Half-written answer')

  fireEvent.click(screen.getByRole('link', { name: /Back to queue/ }))
  const row = (await screen.findByRole('link', { name: 'Printer jam' })).closest('tr')!
  expect(within(row).getByText('Draft')).toBeInTheDocument()
})

test('closing the tab with an unsent draft asks for confirmation; with none it does not', async () => {
  mockFetch(routes())
  renderApp('/tickets/IT-1')
  const box = await screen.findByRole('textbox', { name: /Public reply|Internal note/ })
  const quiet = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(quiet)
  expect(quiet.defaultPrevented).toBe(false)
  fireEvent.change(box, { target: { value: 'Not sent yet' } })
  const guarded = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(guarded)
  expect(guarded.defaultPrevented).toBe(true)
})

test('a long message starts clamped and can be expanded', async () => {
  mockFetch(routes())
  renderApp('/tickets/IT-2')
  const more = await screen.findByRole('button', { name: 'Show full message' })
  expect(more).toHaveAttribute('aria-expanded', 'false')
  expect(screen.getByText(/Line 1 of a long email/)).toHaveClass('clamped')
  fireEvent.click(more)
  expect(screen.getByRole('button', { name: 'Show less' })).toHaveAttribute('aria-expanded', 'true')
  expect(screen.getByText(/Line 1 of a long email/)).not.toHaveClass('clamped')
})

test('previous and next are absent on a direct link, and Back goes to a generic queue', async () => {
  mockFetch(routes())
  renderApp('/tickets/IT-1')
  expect(await screen.findByRole('heading', { name: 'Printer jam', level: 2 })).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: /Next ticket/ })).not.toBeInTheDocument()
  expect(screen.getByRole('link', { name: /Back to queue/ })).toHaveAttribute('href', '/queue/all')
})
