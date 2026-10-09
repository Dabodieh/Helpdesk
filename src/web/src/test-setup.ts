import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(() => cleanup())

// Tests run as on a large desktop window (the properties panel starts open there).
Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1600 })
