/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Same-origin to the browser, so cookie auth + CSRF behave as in production.
    proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080' },
  },
  test: { environment: 'jsdom', setupFiles: ['./src/test-setup.ts'] },
})
