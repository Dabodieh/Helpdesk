import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { expect, test } from 'vitest'
import { Markdown } from './markdown'

const show = (source: string) => render(<MemoryRouter><Markdown source={source} /></MemoryRouter>)

test('renders emphasis, lists and safe links', () => {
  const { container } = show('# Title\n\nSome **bold** text.\n\n- one\n- two\n\n[site](https://example.test/a)')
  expect(screen.getByRole('heading', { name: 'Title' })).toBeInTheDocument()
  expect(container.querySelector('strong')?.textContent).toBe('bold')
  expect(container.querySelectorAll('li')).toHaveLength(2)
  expect(screen.getByRole('link', { name: 'site' })).toHaveAttribute('href', 'https://example.test/a')
})

test('never emits raw HTML or scriptable URLs', () => {
  const { container } = show('<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n[bad](javascript:alert(1))\n\n[data](data:text/html,x)')
  expect(container.querySelector('script')).toBeNull()
  expect(container.querySelector('img')).toBeNull()
  for (const a of container.querySelectorAll('a')) expect(a.getAttribute('href') ?? '').not.toMatch(/^(javascript|data):/i)
  expect(container.textContent).toContain('<script>alert(1)</script>')
})
