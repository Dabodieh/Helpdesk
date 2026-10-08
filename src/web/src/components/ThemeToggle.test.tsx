import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import ThemeToggle from './ThemeToggle'

afterEach(() => { localStorage.clear(); vi.restoreAllMocks() })

test('defaults to dark, switches explicitly and restores the saved preference', () => {
  const view = render(<ThemeToggle />)
  expect(document.documentElement.dataset.theme).toBe('dark')
  fireEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }))
  expect(document.documentElement.dataset.theme).toBe('light')
  view.unmount()
  render(<ThemeToggle />)
  expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeInTheDocument()
})

test('theme switching works when preference storage is unavailable', () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
  render(<ThemeToggle />)
  fireEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }))
  expect(document.documentElement.dataset.theme).toBe('light')
})
