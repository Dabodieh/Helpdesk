import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, makeDetail, makeMessage, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const routes = () => ({
  ...workspaceRoutes(agentMe()),
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page([makeMessage()]),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
  'GET /api/tickets/t1/followers': () => ({ items: [], following: false }),
})

test('a public reply can insert a link to an offered article, and an internal note has no article picker', async () => {
  const calls = mockFetch({
    ...routes(),
    'GET /api/tickets/t1/article-links?q=': () => [{ slug: 'reset-password', title: 'Reset your password', category: 'Accounts', path: '/portal/knowledge/reset-password' }],
  })
  renderApp('/queue/my/IT-1')
  fireEvent.click(await screen.findByRole('button', { name: 'Insert article link' }))
  const dialog = await screen.findByRole('dialog', { name: 'Link a Knowledge article' })
  fireEvent.click(await within(dialog).findByRole('button', { name: /Reset your password/ }))
  const box = screen.getByRole('textbox', { name: 'Public reply' }) as HTMLTextAreaElement
  expect(box.value).toBe(`Reset your password: ${window.location.origin}/portal/knowledge/reset-password`)
  expect(calls.some((c) => c.method === 'POST')).toBe(false) // inserting never sends

  fireEvent.click(screen.getByRole('radio', { name: /Internal note/ }))
  expect(screen.queryByRole('button', { name: 'Insert article link' })).not.toBeInTheDocument()
})

test('when no article can be offered the picker says why instead of listing anything', async () => {
  mockFetch({ ...routes(), 'GET /api/tickets/t1/article-links?q=': () => [] })
  renderApp('/queue/my/IT-1')
  fireEvent.click(await screen.findByRole('button', { name: 'Insert article link' }))
  const dialog = await screen.findByRole('dialog', { name: 'Link a Knowledge article' })
  expect(await within(dialog).findByText(/only when the requester has a linked account/)).toBeInTheDocument()
  expect(within(dialog).queryAllByRole('listitem')).toHaveLength(0)
})
