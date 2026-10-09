/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Reachable from the Cloudflare tunnel on this machine (it connects to the LAN address); only this public hostname is accepted besides localhost.
    host: true,
    allowedHosts: ['helpdesk.amb-it.co.uk'],
    // Same-origin to the browser, so cookie auth + CSRF behave as in production.
    proxy: {
      '/api': 'http://localhost:5080',
      '/health': 'http://localhost:5080',
      // SignalR (negotiate + WebSocket) for live ticket updates.
      '/hubs': { target: 'http://localhost:5080', ws: true },
    },
  },
  // Playwright specs live in e2e/ and run with `npm run e2e`, never under vitest.
  test: { environment: 'jsdom', setupFiles: ['./src/test-setup.ts'], include: ['src/**/*.test.{ts,tsx}'] },
})
