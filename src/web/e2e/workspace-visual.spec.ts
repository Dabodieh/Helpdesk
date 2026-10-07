import { test } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { Api, devLoginPath, seed } from './support'

// Visual review aid for the ticket workspace (not a regression test):
//   VISUAL=1 [VISUAL_OUT=dir] npm --prefix src/web run e2e -- workspace-visual.spec.ts
// Captures a normal ticket, a ticket with type/fields/followers/related/SLA and a merged ticket in light and dark, wide and narrow.
const OUT = process.env.VISUAL_OUT ?? path.join(os.tmpdir(), 'helpdesk-visual')

test('ticket workspace states', async ({ browser }) => {
  test.skip(!process.env.VISUAL, 'set VISUAL=1 to capture')
  test.setTimeout(240_000)
  const data = await seed()
  const admin = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  const d = `/api/departments/${data.departmentId}`
  const type = await admin.send<{ id: string }>('POST', `${d}/ticket-types`, { name: 'Hardware Request' })
  const field = await admin.send<{ id: string }>('POST', `${d}/ticket-fields`, { label: 'Asset Number', key: 'asset', type: 'ShortText', required: true, appliesToAllTypes: false, agentVisible: true, requesterVisible: false })
  await admin.send('POST', `${d}/ticket-fields`, { label: 'Location', key: 'location', type: 'ShortText', required: false, appliesToAllTypes: true, agentVisible: true, requesterVisible: false })
  await admin.send('PUT', `${d}/ticket-types/${type.id}/fields`, { fields: [{ fieldId: field.id }] })
  const hours = await admin.send<{ id: string }>('POST', `${d}/business-hours`, {
    name: 'Office', timeZone: 'Europe/London', exceptions: [],
    weekly: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'].map((day) => ({ day, intervals: [{ start: '00:00', end: '23:59' }] })),
  })
  await admin.send('POST', `${d}/sla-policies`, { name: 'Standard', calendarId: hours.id, firstResponseMinutes: 60, resolutionMinutes: 1, pauseWhenPending: true, approachingLeadMinutes: 30, conditions: [] })
  await admin.dispose()

  const lead = await Api.signIn(data.actor)
  const mk = (subject: string) =>
    lead.send<{ id: string; reference: string }>('POST', `${d}/tickets`, { subject, description: 'The laptop will not boot.', requester: { email: 'dana@example.org', displayName: 'Dana Whitfield' } })
  const rich = await mk('Replace laptop for Dana')
  const merged = await mk('Duplicate: laptop broken')
  const dup = await mk('VPN issue')
  await lead.send('PATCH', `/api/tickets/${rich.id}/properties`, { version: (await lead.get<{ version: string }>(`/api/tickets/${rich.id}/properties`)).version, ticketTypeId: type.id, fields: { location: 'Glasgow Office' } })
  await lead.send('POST', `/api/tickets/${rich.id}/followers/me`, {})
  await lead.send('POST', `/api/tickets/${rich.id}/links`, { ticketId: dup.id, type: 'DuplicateOf' })
  const r = await lead.get<{ version: string }>(`/api/tickets/${rich.id}`)
  const m = await lead.get<{ version: string }>(`/api/tickets/${merged.id}`)
  await lead.send('POST', `/api/tickets/${rich.id}/merge`, { version: r.version, sources: [{ ticketId: merged.id, version: m.version }] })
  await lead.dispose()

  for (const scheme of ['light', 'dark'] as const) {
    for (const [label, width] of [['wide', 1500], ['narrow', 1100]] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width, height: 900 } })
      const page = await context.newPage()
      const shot = (name: string) => page.screenshot({ path: path.join(OUT, `${name}-${label}-${scheme}.png`) })
      await page.goto(devLoginPath(data.actor, `/queue/my/${data.first.reference}`))
      await page.getByRole('region', { name: 'Ticket' }).getByRole('heading', { level: 2 }).waitFor()
      await page.waitForTimeout(800)
      await shot('1-normal')
      await page.goto(`/queue/all/${rich.reference}`)
      await page.getByRole('region', { name: 'Ticket' }).getByRole('heading', { level: 2 }).waitFor()
      if (label === 'narrow') await page.getByRole('button', { name: 'Properties', exact: true }).click()
      await page.waitForTimeout(1200)
      await shot('2-rich')
      await page.goto(`/tickets/${merged.reference}`)
      await page.getByRole('region', { name: 'Merged ticket' }).waitFor()
      if (label === 'narrow') await page.getByRole('button', { name: 'Properties', exact: true }).click()
      await page.waitForTimeout(800)
      await shot('3-merged')
      await context.close()
    }
  }
})
