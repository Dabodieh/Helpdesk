import { test } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid (not a regression test): VISUAL=1 [VISUAL_OUT=dir] npm --prefix src/web run e2e -- dashboard-visual.spec.ts
test('department dashboard', async ({ browser }) => {
  test.skip(!process.env.VISUAL, 'set VISUAL=1 to capture')
  test.setTimeout(180_000)
  const data = await seed()
  const admin = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  const run = Date.now().toString(36)
  const deptAdmin = { subject: `dash-${run}`, name: 'Dept Admin', email: `dash-${run}@example.test` }
  const adminId = (await (await Api.signIn(deptAdmin)).get<{ id: string }>('/api/me')).id
  await admin.send('PUT', `/api/departments/${data.departmentId}/members/${adminId}`, { role: 'DepartmentAdmin' })
  const d = `/api/departments/${data.departmentId}`
  const hours = await admin.send<{ id: string }>('POST', `${d}/business-hours`, {
    name: 'Office', timeZone: 'Europe/London', exceptions: [],
    weekly: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map((day) => ({ day, intervals: [{ start: '00:00', end: '23:59' }] })),
  })
  await admin.send('POST', `${d}/sla-policies`, { name: 'Standard', calendarId: hours.id, firstResponseMinutes: 1, resolutionMinutes: 1, pauseWhenPending: false, approachingLeadMinutes: 30, conditions: [] })
  await admin.dispose()
  const lead = await Api.signIn(data.actor)
  await lead.send('POST', `${d}/tickets`, { subject: 'Unassigned VPN issue', description: 'x', requester: { email: 'r@example.test', displayName: 'R' } })
  await new Promise((r) => setTimeout(r, 70_000)) // let the one-minute targets pass
  await lead.dispose()
  for (const scheme of ['light', 'dark'] as const) {
    const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1400, height: 900 } })
    const page = await context.newPage()
    await page.goto(devLoginPath(deptAdmin, `/dashboard/${data.departmentId}`))
    await page.getByRole('heading', { name: /dashboard/ }).waitFor()
    await page.getByRole('list', { name: 'Needs attention now' }).waitFor()
    await page.waitForTimeout(500)
    await page.screenshot({ path: path.join(process.env.VISUAL_OUT ?? os.tmpdir(), `dashboard-${scheme}.png`) })
    await context.close()
  }
})
