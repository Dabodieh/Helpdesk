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
