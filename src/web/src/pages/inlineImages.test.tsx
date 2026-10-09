import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { agentMe, json, makeDetail, makeMessage, mockFetch, page, renderApp, workspaceRoutes } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const ID = '0a1b2c3d-0000-4000-8000-000000000001'
const png = () => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'shot.png', { type: 'image/png' })

const routes = (enabled: boolean, messages = [makeMessage()]) => ({
  ...workspaceRoutes(agentMe()),
  'GET /api/ticket-capabilities': () => ({ attachmentsEnabled: enabled, maxInlineImageBytes: 3 * 1024 * 1024 }),
  'GET /api/tickets/IT-1': () => makeDetail(),
  'GET /api/tickets/t1/messages': () => page(messages),
  'GET /api/tickets/t1/activity': () => page([]),
  'GET /api/tickets/t1/links': () => [],
  'GET /api/tickets/t1/followers': () => ({ items: [], following: false }),
})

const paste = (file: File) => fireEvent.paste(screen.getByRole('textbox', { name: 'Public reply' }), { clipboardData: { files: [file], types: ['Files'] } })

test('a pasted image is uploaded, shown as a removable thumbnail and named in the message that is sent', async () => {
  const calls = mockFetch({
    ...routes(true),
    'POST /api/tickets/t1/inline-images': () => ({ id: ID, url: `/api/tickets/IT-1/attachments/${ID}`, contentType: 'image/png', size: 4 }),
    'POST /api/tickets/t1/messages': (c) =>
      json({ message: makeMessage({ id: 'm2', authorType: 'Agent', body: (c.body as { body: string }).body }), ticket: makeDetail({ version: 'v2' }) }, 201),
  })
  renderApp('/queue/my/IT-1')
  await screen.findByRole('textbox', { name: 'Public reply' })
  await waitFor(() => expect(calls.some((c) => c.path === '/api/ticket-capabilities')).toBe(true))
  await screen.findByRole('textbox', { name: 'Public reply' })
  await new Promise((r) => setTimeout(r, 50))

  paste(png())
  const remove = await screen.findByRole('button', { name: 'Remove image shot.png' })
  const upload = calls.find((c) => c.path === '/api/tickets/t1/inline-images')!
  expect(upload.method).toBe('POST')
  expect(upload.headers['Content-Type']).toBe('image/png')
  expect(upload.headers['X-CSRF-TOKEN']).toBe('tok')

  fireEvent.click(remove) // removed images are not sent
  expect(screen.queryByRole('button', { name: 'Remove image shot.png' })).not.toBeInTheDocument()
  paste(png())
  await screen.findByRole('button', { name: 'Remove image shot.png' })

  fireEvent.change(screen.getByRole('textbox', { name: 'Public reply' }), { target: { value: 'See below' } })
  fireEvent.click(screen.getByRole('button', { name: 'Post public reply' }))
  await waitFor(() => expect(calls.some((c) => c.path === '/api/tickets/t1/messages' && c.method === 'POST')).toBe(true))
  const sent = calls.find((c) => c.path === '/api/tickets/t1/messages' && c.method === 'POST')!.body as { body: string }
  expect(sent.body).toBe(`See below\n[image:${ID}]`)
})

test('while uploads are switched off the paste is refused with a short message and nothing is uploaded', async () => {
  const calls = mockFetch(routes(false))
  renderApp('/queue/my/IT-1')
  await screen.findByRole('textbox', { name: 'Public reply' })
  await waitFor(() => expect(calls.some((c) => c.path === '/api/ticket-capabilities')).toBe(true))
  await new Promise((r) => setTimeout(r, 50))

  paste(png())
  expect(await screen.findByText('Image uploads are unavailable.')).toBeInTheDocument()
  expect(calls.some((c) => c.path.includes('inline-images'))).toBe(false)
  expect(screen.queryByRole('list', { name: 'Pasted images' })).not.toBeInTheDocument()
})

test('a pasted file that is not a PNG, JPEG or GIF is refused before any upload', async () => {
  const calls = mockFetch(routes(true))
  renderApp('/queue/my/IT-1')
  await screen.findByRole('textbox', { name: 'Public reply' })
  await waitFor(() => expect(calls.some((c) => c.path === '/api/ticket-capabilities')).toBe(true))
  await new Promise((r) => setTimeout(r, 50))

  paste(new File(['<svg/>'], 'x.svg', { type: 'image/svg+xml' }))
  expect(await screen.findByText('Only PNG, JPEG and GIF images can be added.')).toBeInTheDocument()
  expect(calls.some((c) => c.path.includes('inline-images'))).toBe(false)
})

test('a message shows its pasted image inline from its own attachment, and never an image it does not hold', async () => {
  const other = '0a1b2c3d-0000-4000-8000-000000000002'
  mockFetch(
    routes(true, [
      makeMessage({
        id: 'm9',
        authorType: 'Agent',
        body: `Here:\n[image:${ID}]\nand [image:${other}] and <img src="x" onerror="alert(1)">`,
        attachments: [{ id: ID, fileName: 'pasted-image.png', contentType: 'image/png', size: 4, isInline: true, state: 'Stored', scanState: 'Clean', downloadable: true }],
      }),
    ]),
  )
  const { container } = renderApp('/queue/my/IT-1')
  const img = await screen.findByAltText('Pasted image')
  expect(img).toHaveAttribute('src', `/api/tickets/t1/attachments/${ID}`)
  expect(container.querySelectorAll('.msg-body img')).toHaveLength(1)
  expect(screen.getByText('[image not available]')).toBeInTheDocument()
  expect(screen.getByText(/<img src="x" onerror/)).toBeInTheDocument() // typed markup stays text
  expect(screen.queryByRole('list', { name: 'Attachments' })).not.toBeInTheDocument() // the image is not listed twice
})
