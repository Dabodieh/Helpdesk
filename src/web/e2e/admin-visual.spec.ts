import { test } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid for the administration shell and a conversation with an internal note (not a regression test):
//   VISUAL=1 [VISUAL_OUT=dir] npm --prefix src/web run e2e -- admin-visual.spec.ts
const OUT = process.env.VISUAL_OUT ?? path.join(os.tmpdir(), 'helpdesk-visual')
const ADMIN = { subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' }

test('administration and conversation states', async ({ browser }) => {
  test.skip(!process.env.VISUAL, 'set VISUAL=1 to capture')
  test.setTimeout(240_000)
  const data = await seed()
  const lead = await Api.signIn(data.actor)
  const ticket = await lead.get<{ id: string }>(`/api/tickets/${data.first.reference}`)
  await lead.send('POST', `/api/tickets/${ticket.id}/messages`, { kind: 'Public', body: 'Hello, we are looking at the printer now.' })
  await lead.send('POST', `/api/tickets/${ticket.id}/messages`, { kind: 'Internal', body: 'Checked the logs: toner sensor fault, parts ordered.' })
  await lead.dispose()

  for (const scheme of ['light', 'dark'] as const) {
    for (const [label, width] of [['wide', 1500], ['narrow', 1100]] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width, height: 900 } })
      const page = await context.newPage()
      const shot = (name: string) => page.screenshot({ path: path.join(OUT, `${name}-${label}-${scheme}.png`) })
      await page.goto(devLoginPath(ADMIN, '/admin'))
      await page.getByRole('heading', { name: 'Administration', level: 1 }).waitFor()
      await page.waitForTimeout(600)
      await shot('a1-departments')
      await page.goto(`/admin/departments/${data.departmentId}`)
      await page.getByRole('list', { name: 'Setup status' }).waitFor()
      await page.waitForTimeout(600)
      await shot('a2-overview')
      await page.goto(`/admin/departments/${data.departmentId}?section=members`)
      await page.getByRole('tab', { name: 'Members', selected: true }).waitFor()
      await page.waitForTimeout(600)
      await shot('a3-members')
      await page.goto(`/admin/departments/${data.departmentId}?section=ticketing`)
      await page.getByRole('tab', { name: 'Ticketing', selected: true }).waitFor()
      await page.waitForTimeout(600)
      await shot('a4-ticketing')
      await page.getByRole('tab', { name: 'Request forms' }).click()
      await page.getByRole('button', { name: 'New request form' }).click()
      await page.waitForTimeout(500)
      await shot('a5-form-dialog')
      await context.close()

      const ctx2 = await browser.newContext({ colorScheme: scheme, viewport: { width, height: 900 } })
      const p2 = await ctx2.newPage()
      await p2.goto(devLoginPath(data.actor, `/queue/my/${data.first.reference}`))
      await p2.getByRole('region', { name: 'Ticket' }).getByRole('heading', { level: 2 }).waitFor()
      await p2.waitForTimeout(1000)
      await p2.screenshot({ path: path.join(OUT, `b1-note-${label}-${scheme}.png`) })
      await ctx2.close()
    }
  }
})
