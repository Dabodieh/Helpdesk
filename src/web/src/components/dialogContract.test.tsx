import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { shouldIgnoreShortcut } from '../lib/shortcuts'
import Dialog from './Dialog'

afterEach(() => vi.restoreAllMocks())

test('workspace shortcuts are inert while a modal dialog is open (UX-001)', () => {
  const press = () => {
    const e = new KeyboardEvent('keydown', { key: 'x', bubbles: true })
    document.body.dispatchEvent(e)
    return shouldIgnoreShortcut(Object.defineProperty(e, 'target', { value: document.body }))
  }
  expect(press()).toBe(false)
  render(
    <Dialog title="A dialog" onClose={() => undefined}>
      <button type="button">inside</button>
    </Dialog>,
  )
  expect(press()).toBe(true)
})

test('closing a dialog with typed work asks first; a clean dialog closes at once (UX-008)', () => {
  const onClose = vi.fn()
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
  const { rerender } = render(
    <Dialog title="Draft" onClose={onClose} dirty>
      <input aria-label="text" />
    </Dialog>,
  )
  fireEvent.keyDown(document, { key: 'Escape' })
  expect(confirm).toHaveBeenCalledTimes(1)
  expect(onClose).not.toHaveBeenCalled() // kept

  confirm.mockReturnValue(true)
  fireEvent.click(screen.getByRole('button', { name: 'Close dialog' }))
  expect(onClose).toHaveBeenCalledTimes(1)

  onClose.mockClear()
  confirm.mockClear()
  rerender(
    <Dialog title="Draft" onClose={onClose}>
      <input aria-label="text" />
    </Dialog>,
  )
  fireEvent.keyDown(document, { key: 'Escape' })
  expect(confirm).not.toHaveBeenCalled()
  expect(onClose).toHaveBeenCalledTimes(1)
})
