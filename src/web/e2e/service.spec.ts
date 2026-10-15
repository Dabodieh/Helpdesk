import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { Api, devLoginPath, seed } from './support'

// Phase 5: business hours, SLA policy and an automation rule configured through the UI, then seen at work on a real ticket.

async function noSeriousViolations(page: Page, label: string) {
  const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  const serious = violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  expect(serious.map((v) => `${label}: ${v.id} (${v.nodes.length}) ${v.help}`)).toEqual([])
}

// The ticket properties rail is docked open from 1400 px wide (behind the Properties button below that).
test.use({ viewport: { width: 1440, height: 900 } })

test('administrator configures hours, a policy and a routing rule; a new ticket gets its SLA and assignee; dry run explains', async ({ page }) => {
  const data = await seed()
  // Make the seeded lead a department administrator (as platform admin) so one signed-in person can configure and read tickets.
  const lead = await Api.signIn(data.actor)
  const leadId = (await lead.get<{ id: string }>('/api/me')).id
  await lead.dispose()
  const platform = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  await platform.send('PUT', `/api/departments/${data.departmentId}/members/${leadId}`, { role: 'DepartmentAdmin' })
  await platform.dispose()

  await page.goto(devLoginPath(data.actor, `/admin/departments/${data.departmentId}`))

  // Business hours
  await page.getByRole('tab', { name: 'Business hours' }).click()
  await page.getByRole('button', { name: 'New business hours' }).click()
  const hours = page.getByRole('dialog')
  await hours.getByLabel('Name').fill('Office hours')
  await hours.getByLabel('Time zone').fill('Europe/London')
  await noSeriousViolations(page, 'business hours dialog')
  await hours.getByRole('button', { name: 'Save business hours' }).click()
  await expect(page.getByRole('cell', { name: 'Office hours', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Europe/London' })).toBeVisible()

  // SLA policy
  await page.getByRole('tab', { name: 'SLA policies' }).click()
  await page.getByRole('button', { name: 'New SLA policy' }).click()
  const policy = page.getByRole('dialog')
  await policy.getByLabel('Name').fill('Standard support')
  await policy.getByLabel('Business hours').selectOption({ label: 'Office hours (Europe/London)' })
  await policy.getByLabel('Pause the resolution clock while the ticket is Pending').check()
  await noSeriousViolations(page, 'SLA policy dialog')
  await policy.getByRole('button', { name: 'Save policy' }).click()
  await expect(page.getByRole('cell', { name: 'Standard support', exact: true })).toBeVisible()
  await expect(page.getByText(/First response 1h/)).toBeVisible()

  // Automation rule: network tickets go to the department's lead
  await page.getByRole('tab', { name: 'Automations' }).click()
  await page.getByRole('button', { name: 'New rule' }).click()
  const rule = page.getByRole('dialog')
  await rule.getByLabel('Name').fill('Network routing')
  await rule.getByRole('button', { name: 'Add condition' }).click()
  await rule.getByLabel('Condition 1 field').selectOption('subjectContains')
  await rule.getByLabel('Condition 1 value').fill('network')
  await rule.getByLabel('Action 1 type').selectOption('assignAgent')
  await rule.getByLabel('Action 1 value').selectOption({ label: data.actor.name })
  await noSeriousViolations(page, 'rule editor dialog')
  await rule.getByRole('button', { name: 'Save rule' }).click()
  await expect(page.locator('strong', { hasText: 'Network routing' })).toBeVisible() // the row's edit button repeats the name for screen readers
  await expect(page.getByText(new RegExp(`Assign agent: ${data.actor.name}`))).toBeVisible()

  // A new ticket: SLA applied, assignee set by the rule.
  const agent = await Api.signIn(data.actor)
  const subject = `Network outage ${Date.now().toString(36)}`
  const created = await agent.send<{ reference: string }>('POST', `/api/departments/${data.departmentId}/tickets`, {
    subject,
    description: 'Switch is down',
    requester: { email: `net-${Date.now().toString(36)}@example.test`, displayName: 'Net Requester' },
  })
  await agent.dispose()

  await page.goto('/queue/all')
  const row = page.getByRole('row').filter({ hasText: subject })
  await expect(row.getByText(/On track|Near deadline|Due soon/).first()).toBeAttached()
  await row.getByRole('link', { name: subject }).click()
  const ticket = page.getByRole('region', { name: 'Ticket', exact: true })
  await expect(ticket.getByRole('combobox', { name: 'Assignee', exact: true })).not.toHaveValue('')
  // The service level lives in the properties rail (SlaPanel); the header strip it replaced is no longer rendered.
  await expect(ticket.getByRole('region', { name: 'Service level' })).toBeVisible()
  await ticket.getByRole('button', { name: 'SLA details' }).click()
  const details = page.getByRole('dialog')
  await expect(details.getByText('Standard support', { exact: true })).toBeVisible()
  await expect(details.getByText(/SLA applied/)).toBeVisible()
  await noSeriousViolations(page, 'SLA details dialog')
  await page.keyboard.press('Escape')

  // SLA filter and sort in the queue
  await page.goto('/queue/all')
  await page.getByRole('search', { name: 'Filter tickets' }).getByText('Filters', { exact: true }).click() // the extra filters sit in a collapsed group
  await page.getByRole('combobox', { name: 'Service level', exact: true }).selectOption('duesoon')
  await page.getByRole('combobox', { name: 'Service level', exact: true }).selectOption('')
  await page.getByRole('combobox', { name: 'Sort', exact: true }).selectOption('sla_due_asc')
  await expect(page).toHaveURL(/sort=sla_due_asc/)

  // History and dry run in the administration area
  await page.goto(`/admin/departments/${data.departmentId}`)
  await page.getByRole('tab', { name: 'Automations' }).click()
  await page.getByText('History', { exact: true }).click()
  await expect(page.getByRole('cell', { name: created.reference })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Matched' }).first()).toBeVisible()
  await noSeriousViolations(page, 'automation history')
  await page.getByText('Rules', { exact: true }).click()
  await page.getByRole('button', { name: 'Test against ticket' }).click()
  const test_ = page.getByRole('dialog')
  await test_.getByLabel('Ticket reference').fill(created.reference)
  await test_.getByRole('button', { name: 'Test', exact: true }).click()
  const result = test_.getByRole('region', { name: 'Test result' })
  await expect(result.getByText('Matched')).toBeVisible()
  await expect(result.getByText(/Nothing was changed/)).toBeVisible()
  await noSeriousViolations(page, 'dry run result')
})
