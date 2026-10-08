import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { Api, devLoginPath, seed, type Seed } from './support'

// Phase 4 flows against the real backend: canned responses, saved views, bulk actions, realtime and accessibility.

async function signIn(page: Page, data: Seed, path = '/queue/my') {
  await page.goto(devLoginPath(data.actor, path))
  await expect(page.getByRole('heading', { name: 'My Queue', level: 1 })).toBeVisible()
}

async function expectNoSeriousViolations(page: Page, label: string, include?: string) {
  let builder = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
  if (include) builder = builder.include(include)
  const { violations } = await builder.analyze()
  const serious = violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  expect(serious.map((v) => `${label}: ${v.id} (${v.nodes.length}) ${v.help}`)).toEqual([])
}

test('canned response is inserted into the draft, modes are explicit, and the reply posts only on submit', async ({ page }) => {
  const data = await seed()
  const lead = await Api.signIn(data.actor)
  await lead.send('POST', '/api/canned-responses', { scope: 'Personal', title: 'Technician coming', body: 'A technician is on the way.' })
  await lead.dispose()

  await signIn(page, data)
  await page.getByRole('link', { name: data.first.subject }).click()
  const ticket = page.getByRole('region', { name: 'Ticket' })
  await expect(ticket.getByRole('heading', { name: data.first.subject, level: 2 })).toBeVisible()

  await ticket.getByRole('button', { name: /Insert response/ }).click()
  await page.getByRole('dialog').getByRole('button', { name: /Technician coming/ }).click()
  const box = ticket.getByRole('textbox', { name: 'Public reply' })
  await expect(box).toHaveValue('A technician is on the way.')
  // Nothing was posted by inserting.
  await expect(ticket.getByRole('article', { name: /^Public reply from/ })).toHaveCount(0)

  // Switching to an internal note keeps the draft and changes the explicit mode.
  await ticket.locator('label', { hasText: 'Internal note' }).click()
  await expect(ticket.getByText('Internal note — not visible to the requester')).toBeVisible()
  await expect(ticket.getByRole('textbox', { name: 'Internal note' })).toHaveValue('A technician is on the way.')
  await ticket.locator('label', { hasText: 'Public reply' }).click()

  // Enter alone never sends.
  await box.press('Enter')
  await expect(ticket.getByRole('article', { name: /^Public reply from/ })).toHaveCount(0)
  await ticket.getByRole('button', { name: 'Post public reply' }).click()
  await expect(ticket.getByRole('article', { name: /^Public reply from/ })).toContainText('A technician is on the way.')
})

test('a saved view is pinned in the sidebar and survives a reload', async ({ page }) => {
  const data = await seed()
  await signIn(page, data)
  await page.getByRole('combobox', { name: 'Priority', exact: true }).selectOption('Normal')
  await page.getByRole('button', { name: 'Views' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('View name').fill('E2E normal work')
  await dialog.getByLabel('Pin to the sidebar').check()
  await dialog.getByRole('button', { name: 'Save view' }).click()
  await expect(page).toHaveURL(/view=/)

  await page.reload()
  const pinned = page.getByRole('navigation').getByRole('link', { name: /E2E normal work/ })
  await expect(pinned).toBeVisible()
  await pinned.click()
  await expect(page).toHaveURL(/view=/)
  await expect(page.getByRole('link', { name: data.first.subject })).toBeVisible()
})

test('bulk action changes priority on the selection and reports the result', async ({ page }) => {
  const data = await seed()
  await signIn(page, data)
  await page.getByRole('checkbox', { name: `Select ${data.first.reference}` }).check()
  await page.getByRole('checkbox', { name: `Select ${data.second.reference}` }).check()
  const bar = page.getByRole('region', { name: 'Bulk actions' })
  await expect(bar.getByText('2 selected')).toBeVisible()
  await bar.getByLabel('Priority').selectOption('Urgent')
  await bar.getByRole('button', { name: 'Apply' }).click()
  await expect(bar.getByRole('status')).toContainText('2 updated')
  await page.getByRole('link', { name: data.first.subject }).click()
  await expect(page.getByRole('region', { name: 'Ticket' }).getByRole('combobox', { name: 'Priority', exact: true })).toHaveValue('Urgent')
})

test('a change made in another session appears without reloading', async ({ page }) => {
  const data = await seed()
  await signIn(page, data)
  await expect(page.getByText(/Live updates on/)).toBeVisible()
  await page.getByRole('link', { name: data.first.subject }).click()
  const ticket = page.getByRole('region', { name: 'Ticket' })
  const priority = ticket.getByRole('combobox', { name: 'Priority', exact: true })
  await expect(priority).toHaveValue('Normal')

  // Second session: same user, API only.
  const other = await Api.signIn(data.actor)
  const detail = await other.get<{ version: string }>(`/api/tickets/${data.first.reference}`)
  await other.send('PATCH', `/api/tickets/${data.first.reference}`, { version: detail.version, priority: 'High' })
  await other.dispose()

  await expect(priority).toHaveValue('High', { timeout: 15_000 })
})

test('accessibility: queue, ticket workspace, dialogs and administration have no serious violations', async ({ page }) => {
  const data = await seed()
  await signIn(page, data)
  await page.getByRole('link', { name: data.first.subject }).click()
  await expect(page.getByRole('region', { name: 'Ticket' }).getByRole('heading', { name: data.first.subject, level: 2 })).toBeVisible()
  await expectNoSeriousViolations(page, 'workspace')

  await page.getByRole('button', { name: 'Views' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expectNoSeriousViolations(page, 'views dialog')
  await page.keyboard.press('Escape')

  await page.goto(`/admin/departments/${data.departmentId}`)
  await expect(page.getByRole('tab', { name: 'Members' })).toBeVisible()
  await expectNoSeriousViolations(page, 'department overview')
  await page.getByRole('tab', { name: 'Members' }).click()
  await expectNoSeriousViolations(page, 'department members')

  // Administration dialogs (platform admin).
  await page.goto(devLoginPath({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' }, '/admin'))
  await page.getByRole('button', { name: 'New department' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expectNoSeriousViolations(page, 'new department dialog')
})
