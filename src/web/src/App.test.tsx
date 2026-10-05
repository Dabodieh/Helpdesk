import { render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import App from './App'

afterEach(() => vi.unstubAllGlobals())

test('shows API status from the readiness endpoint', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response('Healthy', { status: 200 })))
  render(<App />)
  expect(await screen.findByText('API: Healthy')).toBeInTheDocument()
})
