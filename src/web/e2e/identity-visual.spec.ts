import { test } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid (not a regression test): VISUAL=1 npm --prefix src/web run e2e -- identity-visual.spec.ts
test('requester identity resolution', async ({ browser }) => {
  test.skip(!process.env.VISUAL, 'set VISUAL=1 to capture')
  const data = await seed()
  const admin = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  const run = Date.now().toString(36)
  const email = `jane-${run}@example.test`
  const lead = await Api.signIn(data.actor)
  await lead.send('POST', `/api/departments/${data.departmentId}/tickets`, { subject: 'Printer jam', description: 'x', requester: { email, displayName: 'Jane Smith' } })
  await Api.signIn({ subject: `jane-${run}`, name: 'Jane Jones', email })
  const deptAdmin = { subject: `idadmin-${run}`, name: 'Dept Admin', email: `idadmin-${run}@example.test` }
  const adminId = (await (await Api.signIn(deptAdmin)).get<{ id: string }>('/api/me')).id
  await admin.send('PUT', `/api/departments/${data.departmentId}/members/${adminId}`, { role: 'DepartmentAdmin' })
  await admin.dispose()
  const context = await browser.newContext({ colorScheme: 'light', viewport: { width: 1300, height: 800 } })
  const page = await context.newPage()
  await page.goto(devLoginPath(deptAdmin, `/admin/departments/${data.departmentId}?section=requesters`))
  await page.getByRole('heading', { name: /Needs attention/ }).waitFor()
  await page.screenshot({ path: path.join(process.env.VISUAL_OUT ?? os.tmpdir(), 'identity-list.png') })
  await page.getByRole('button', { name: 'Resolve Jane Smith' }).click()
  await page.getByRole('button', { name: 'Select this account' }).click()
  await page.waitForTimeout(400)
  await page.screenshot({ path: path.join(process.env.VISUAL_OUT ?? os.tmpdir(), 'identity-dialog.png') })
})
