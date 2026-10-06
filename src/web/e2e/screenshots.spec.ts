import { test } from '@playwright/test'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid, not a regression test: `SCREENSHOTS=1 npm --prefix src/web run e2e -- screenshots.spec.ts`
// writes light/dark screenshots of the main screens to docs/design/agent-workspace/screenshots/.
const OUT = path.resolve(process.cwd(), '../../docs/design/agent-workspace/screenshots')

for (const scheme of ['light', 'dark'] as const) {
  test(`screenshots (${scheme})`, async ({ browser }) => {
    test.skip(!process.env.SCREENSHOTS, 'set SCREENSHOTS=1 to capture')
    const data = await seed()
    const lead = await Api.signIn(data.actor)
    await lead.send('POST', '/api/canned-responses', { scope: 'Personal', title: 'Technician coming', body: 'A technician is on the way.' })
    await lead.dispose()
    const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1440, height: 900 } })
    const page = await context.newPage()
    const shot = (name: string) => page.screenshot({ path: path.join(OUT, `${name}-${scheme}.png`) })

    await page.goto(devLoginPath(data.actor, '/queue/my'))
    await page.getByRole('link', { name: data.first.subject }).waitFor()
    await page.getByRole('link', { name: data.first.subject }).click()
    await page.getByRole('region', { name: 'Ticket' }).getByRole('heading', { level: 2 }).waitFor()
    await page.getByRole('textbox', { name: 'Public reply' }).fill('Thanks, a technician is on the way.')
    await shot('workspace')

    await page.getByRole('checkbox', { name: `Select ${data.first.reference}` }).check()
    await shot('bulk-selection')
    await page.getByRole('checkbox', { name: `Select ${data.first.reference}` }).uncheck()

    await page.keyboard.press('Escape')
    await page.getByRole('button', { name: 'Views' }).click()
    await shot('views-dialog')
    await page.keyboard.press('Escape')

    await page.getByRole('button', { name: /Insert response/ }).click()
    await shot('canned-picker')
    await page.keyboard.press('Escape')

    await page.goto(`/admin/departments/${data.departmentId}`)
    await page.getByRole('tab', { name: 'Teams' }).waitFor()
    await shot('department-admin')
    await context.close()
  })
}

for (const scheme of ['light', 'dark'] as const) {
  test(`service level screenshots (${scheme})`, async ({ browser }) => {
    test.skip(!process.env.SCREENSHOTS, 'set SCREENSHOTS=1 to capture')
    const data = await seed()
    const lead = await Api.signIn(data.actor)
    const leadId = (await lead.get<{ id: string }>('/api/me')).id
    await lead.dispose()
    const platform = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
    await platform.send('PUT', `/api/departments/${data.departmentId}/members/${leadId}`, { role: 'DepartmentAdmin' })
    await platform.dispose()
    const admin = await Api.signIn(data.actor)
    const base = `/api/departments/${data.departmentId}`
    const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'].map((day) => ({ day, intervals: [{ start: '09:00', end: '17:00' }] }))
    const calendar = await admin.send<{ id: string }>('POST', `${base}/business-hours`, {
      name: 'Office hours',
      timeZone: 'Europe/London',
      weekly: week,
      exceptions: [{ date: '2026-12-25', name: 'Christmas Day', intervals: [] }],
    })
    await admin.send('POST', `${base}/sla-policies`, { name: 'Standard support', calendarId: calendar.id, firstResponseMinutes: 60, resolutionMinutes: 480, pauseWhenPending: true })
    await admin.send('POST', `${base}/automations`, {
      name: 'Network routing',
      trigger: 'TicketCreated',
      conditions: [{ field: 'subjectContains', operator: 'contains', values: ['network'] }],
      actions: [{ type: 'addInternalNote', text: 'Routed by the network rule' }, { type: 'setPriority', value: 'High' }],
    })
    await admin.send('POST', `${base}/tickets`, { subject: 'Network outage in building 2', description: 'Switch is down', requester: { email: 'net@example.test', displayName: 'Net Requester' } })
    await admin.dispose()

    const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1440, height: 900 } })
    const page = await context.newPage()
    const shot = (name: string) => page.screenshot({ path: path.join(OUT, `${name}-${scheme}.png`) })
    await page.goto(devLoginPath(data.actor, '/queue/all'))
    await page.getByRole('link', { name: 'Network outage in building 2' }).click()
    await page.getByRole('button', { name: 'Details' }).waitFor()
    await shot('sla-ticket')
    await page.goto(`/admin/departments/${data.departmentId}`)
    await page.getByRole('tab', { name: 'Business hours' }).click()
    await page.getByRole('cell', { name: 'Office hours', exact: true }).waitFor()
    await shot('sla-business-hours')
    await page.getByRole('tab', { name: 'SLA policies' }).click()
    await page.getByRole('cell', { name: 'Standard support', exact: true }).waitFor()
    await shot('sla-policies')
    await page.getByRole('tab', { name: 'Automations' }).click()
    await page.getByText('Network routing', { exact: true }).waitFor()
    await shot('automations')
    await page.getByRole('button', { name: 'New rule' }).click()
    await page.getByRole('button', { name: 'Add condition' }).click()
    await shot('automation-editor')
    await context.close()
  })
}
