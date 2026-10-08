import { test } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid (not a regression test): VISUAL=1 [VISUAL_OUT=dir] npm --prefix src/web run e2e -- notifications-visual.spec.ts
test('notification centre', async ({ browser }) => {
  test.skip(!process.env.VISUAL, 'set VISUAL=1 to capture')
  const data = await seed()
  const colleague = await Api.signIn(data.colleague)
  const colleagueId = (await colleague.get<{ id: string }>('/api/me')).id
  const lead = await Api.signIn(data.actor)
  const tickets = await lead.get<{ items: { id: string; version: string }[] }>(`/api/tickets?queue=all&departmentId=${data.departmentId}`)
  for (const t of tickets.items.slice(0, 2)) {
    const full = await lead.get<{ version: string }>(`/api/tickets/${t.id}`)
    await lead.send('PUT', `/api/tickets/${t.id}/assignment`, { version: full.version, assigneeUserId: colleagueId })
  }
  const context = await browser.newContext({ colorScheme: 'light', viewport: { width: 1300, height: 760 } })
  const page = await context.newPage()
  await page.goto(devLoginPath(data.colleague, '/queue/my'))
  await page.getByRole('button', { name: /Notifications, \d+ unread/ }).click()
  await page.getByRole('region', { name: 'Notifications' }).waitFor()
  await page.waitForTimeout(400)
  await page.screenshot({ path: path.join(process.env.VISUAL_OUT ?? os.tmpdir(), 'notifications.png') })
})
