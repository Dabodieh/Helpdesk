import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { Api, devLoginPath, seed, type Seed, type TestUser } from './support'

// Phase 6: the requester portal against the real stack (simulator mail, real PostgreSQL), plus knowledge and administration.

async function noSeriousViolations(page: Page, label: string) {
  const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  const serious = violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  expect(serious.map((v) => `${label}: ${v.id} (${v.nodes.length}) ${v.help} ${JSON.stringify(v.nodes[0]?.target)}`)).toEqual([])
}

const employee = (): TestUser => {
  const id = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
  return { subject: `e2e-emp-${id}`, name: `Emma Employee ${id}`, email: `emma-${id}@example.test` }
}

/** The seeded lead becomes the department administrator; returns an API client for configuration and agent actions. */
async function configure(data: Seed) {
  const lead = await Api.signIn(data.actor)
  const leadId = (await lead.get<{ id: string }>('/api/me')).id
  await lead.dispose()
  const platform = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  await platform.send('PUT', `/api/departments/${data.departmentId}/members/${leadId}`, { role: 'DepartmentAdmin' })
  await platform.dispose()
  return Api.signIn(data.actor)
}

test('a requester submits a request through a form, sees it in My requests, and it is routed and given an SLA on the agent side', async ({ page }) => {
  const data = await seed()
  const admin = await configure(data)
  const base = `/api/departments/${data.departmentId}`
  const formToken = Date.now().toString(36)
  // Ticket type + reusable fields, then a form that creates that type and asks some of the fields (the cost centre is agent-only and belongs to the type).
  const type = await admin.send<{ id: string }>('POST', `${base}/ticket-types`, { name: `Hardware request ${formToken}` })
  const asset = await admin.send<{ id: string }>('POST', `${base}/ticket-fields`, { label: 'Asset tag', type: 'ShortText', required: true })
  const colour = await admin.send<{ id: string }>('POST', `${base}/ticket-fields`, { label: 'Colour', type: 'SingleSelect', options: [{ label: 'Black' }, { label: 'Silver' }] })
  const cost = await admin.send<{ id: string }>('POST', `${base}/ticket-fields`, { label: 'Internal cost centre', type: 'ShortText', requesterVisible: false })
  await admin.send('PUT', `${base}/ticket-types/${type.id}/fields`, { fields: [{ fieldId: cost.id }] })
  const form = await admin.send<{ id: string }>('POST', `${base}/request-forms`, { name: `Hardware issue ${formToken}`, description: 'Something is broken', defaultPriority: 'High', ticketTypeId: type.id })
  await admin.send('PUT', `${base}/request-forms/${form.id}/fields`, { fields: [{ fieldId: asset.id }, { fieldId: colour.id }, { fieldId: cost.id }] })
  const calendar = await admin.send<{ id: string }>('POST', `${base}/business-hours`, {
    name: 'Always',
    timeZone: 'UTC',
    weekly: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map((day) => ({ day, intervals: [{ start: '00:00', end: '24:00' }] })),
  })
  await admin.send('POST', `${base}/sla-policies`, { name: 'Portal SLA', calendarId: calendar.id, firstResponseMinutes: 60, resolutionMinutes: 480 })
  await admin.send('POST', `${base}/automations`, {
    name: 'Laptop routing',
    trigger: 'TicketCreated',
    conditions: [{ field: 'subjectContains', operator: 'contains', values: ['laptop'] }],
    actions: [{ type: 'assignTeam', value: (await admin.get<{ id: string; name: string }[]>(`${base}/teams`))[0]!.id }],
  })

  const emma = employee()
  await page.goto(devLoginPath(emma, '/'))
  await expect(page).toHaveURL(/\/portal$/) // employees land in the portal, not the agent workspace
  await expect(page.getByRole('heading', { name: 'How can we help?' })).toBeVisible()
  await noSeriousViolations(page, 'portal home')

  await page.getByRole('link', { name: 'Submit a request' }).first().click()
  await expect(page.getByRole('heading', { name: 'Submit a request' })).toBeVisible()
  await noSeriousViolations(page, 'choose a form')
  await page.getByRole('link', { name: new RegExp(`Hardware issue ${formToken}`) }).click()
  await expect(page.getByRole('heading', { name: `Hardware issue ${formToken}` })).toBeVisible()
  await expect(page.getByLabel('Internal cost centre')).toHaveCount(0) // hidden fields are never offered

  // Required field errors are tied to the field.
  await page.getByLabel('Subject').fill('Laptop will not boot')
  await page.getByLabel('Describe what you need').fill('It stays on a black screen.')
  await page.getByRole('button', { name: 'Submit request' }).click()
  await expect(page.getByText('Asset tag is required.')).toBeVisible()
  await expect(page.getByLabel(/Asset tag/)).toHaveAttribute('aria-invalid', 'true')
  await noSeriousViolations(page, 'submit form with errors')

  await page.getByLabel(/Asset tag/).fill('A-1042')
  await page.getByLabel(/Colour/).selectOption({ label: 'Silver' })
  await page.getByRole('button', { name: 'Submit request' }).click()
  await expect(page.getByRole('heading', { name: 'Request received' })).toBeVisible()
  const reference = (await page.locator('.portal-success .ref').innerText()).trim()
  expect(reference).toMatch(/^[A-Z0-9]+-\d+$/)

  await page.goto('/portal/requests')
  await expect(page.getByRole('link', { name: /Laptop will not boot/ })).toBeVisible()
  await expect(page.getByText('In progress').first()).toBeVisible()
  await noSeriousViolations(page, 'my requests')

  // Agent side: a normal ticket with SLA, routing and the form values.
  const ticket = await admin.get<{ team: { id: string } | null; priority: string; source: string; type: { displayName: string } | null; fields: { label: string; value: string }[] }>(`/api/tickets/${reference}`)
  expect(ticket.source).toBe('portal')
  expect(ticket.type?.displayName).toBe(`Hardware request ${formToken}`)
  expect(ticket.team).not.toBeNull()
  expect(ticket.priority).toBe('High')
  expect(ticket.fields.map((f) => `${f.label}=${f.value}`)).toEqual(expect.arrayContaining(['Asset tag=A-1042', 'Colour=Silver']))
  expect((await admin.get<{ policyName: string }>(`/api/tickets/${reference}/sla`)).policyName).toBe('Portal SLA')

  // An agent sees the type and the configured fields in the ticket details and records the agent-only value.
  await page.goto(devLoginPath(data.actor, `/tickets/${reference}`))
  await page.locator('summary', { hasText: 'Details' }).click()
  await expect(page.getByText('Asset tag').first()).toBeVisible()
  await page.getByRole('button', { name: 'Edit details' }).click()
  await page.getByLabel(/Internal cost centre/).fill('CC-77')
  await noSeriousViolations(page, 'edit ticket details')
  await page.getByRole('button', { name: 'Save details' }).click()
  await expect(page.getByText('CC-77')).toBeVisible()
  await admin.dispose()
})

test('a requester sees the public conversation only, replies, and sees the agent answer', async ({ page }) => {
  const data = await seed()
  const admin = await configure(data)
  const emma = employee()
  const created = await admin.send<{ reference: string; id: string }>('POST', `/api/departments/${data.departmentId}/tickets`, {
    subject: 'Monitor flickers',
    description: 'Flickers every few minutes.',
    requester: { email: emma.email, displayName: emma.name },
  })
  await admin.send('POST', `/api/tickets/${created.reference}/messages`, { kind: 'Public', body: 'Have you tried another cable?' })
  await admin.send('POST', `/api/tickets/${created.reference}/messages`, { kind: 'Internal', body: 'PRIVATE-NOTE probably the panel' })

  await page.goto(devLoginPath(emma, `/portal/requests/${created.reference}`))
  await expect(page.getByRole('heading', { name: 'Monitor flickers' })).toBeVisible()
  await expect(page.getByText('Have you tried another cable?')).toBeVisible()
  await expect(page.getByText('PRIVATE-NOTE')).toHaveCount(0)
  await expect(page.getByText(/internal/i)).toHaveCount(0)
  await expect(page.getByText('Support').first()).toBeVisible()
  await noSeriousViolations(page, 'request detail')

  await page.getByLabel('Add a reply').fill('Yes, the new cable works. Thank you!')
  await page.getByRole('button', { name: 'Send reply' }).click()
  await expect(page.getByText('Yes, the new cable works. Thank you!')).toBeVisible()
  await expect
    .poll(async () => {
      const messages = await admin.get<{ items: { body: string; authorType: string; source: string }[] }>(`/api/tickets/${created.reference}/messages`)
      return messages.items.some((m) => m.body.startsWith('Yes, the new cable') && m.authorType === 'Requester' && m.source === 'portal')
    })
    .toBe(true)

  // The agent answers; the requester's open page picks it up (realtime, with polling as the fallback).
  await admin.send('POST', `/api/tickets/${created.reference}/messages`, { kind: 'Public', body: 'Great, we will close this.' })
  await expect(page.getByText('Great, we will close this.')).toBeVisible({ timeout: 20_000 })
  await expect(page.getByText('PRIVATE-NOTE')).toHaveCount(0)
  await admin.dispose()
})

test('knowledge: search finds published articles, drafts do not exist, and administration works', async ({ page }) => {
  const data = await seed()
  const admin = await configure(data)
  const base = `/api/departments/${data.departmentId}/knowledge`
  const token = Date.now().toString(36)
  const live = await admin.send<{ id: string; slug: string; version: string }>('POST', `${base}/articles`, {
    title: `Reset your password ${token}`,
    summary: 'Steps to reset your password',
    body: '# Steps\n\n1. Open the **self service** page.\n2. Follow the [guide](https://example.test/guide).\n\n<script>alert(1)</script>',
  })
  await admin.send('POST', `${base}/articles/${live.id}/publish`, { version: live.version })
  const draft = await admin.send<{ slug: string }>('POST', `${base}/articles`, { title: `Secret draft ${token}`, body: 'not public' })

  const emma = employee()
  await page.goto(devLoginPath(emma, '/portal/knowledge'))
  await page.getByLabel('Search help articles').fill(`passw ${token}`)
  await page.getByRole('button', { name: 'Search' }).click()
  await expect(page.getByRole('link', { name: `Reset your password ${token}` })).toBeVisible()
  await noSeriousViolations(page, 'knowledge search')

  await page.getByLabel('Search help articles').fill(`Secret draft ${token}`)
  await page.getByRole('button', { name: 'Search' }).click()
  await expect(page.getByText('No articles found.')).toBeVisible()
  await expect(page.getByRole('link', { name: `Secret draft ${token}` })).toHaveCount(0)

  await page.goto(`/portal/knowledge/${live.slug}`)
  await expect(page.getByRole('heading', { name: `Reset your password ${token}`, level: 1 })).toBeVisible()
  await expect(page.locator('.markdown strong')).toHaveText('self service')
  await expect(page.locator('.markdown script')).toHaveCount(0)
  await expect(page.getByText('<script>alert(1)</script>')).toBeVisible() // shown as text, never run
  await page.getByRole('button', { name: 'Yes' }).click()
  await expect(page.getByText('Thanks for the feedback.')).toBeVisible()
  await noSeriousViolations(page, 'knowledge article')

  await page.goto(`/portal/knowledge/${draft.slug}`)
  await expect(page.getByText(/not found/i)).toBeVisible()
  await expect(page.getByRole('heading', { name: /Secret draft/ })).toHaveCount(0)

  // Administration: ticketing (types, fields, forms) and knowledge with dialogs.
  await page.goto(devLoginPath(data.actor, `/admin/departments/${data.departmentId}`))
  await page.getByRole('tab', { name: 'Ticketing' }).click()
  await page.getByRole('tab', { name: 'Ticket fields' }).click()
  await page.getByRole('button', { name: 'New ticket field' }).click()
  await page.getByRole('dialog').getByLabel('Label', { exact: true }).fill('Which system?')
  await page.getByRole('dialog').getByLabel('Type', { exact: true }).selectOption('SingleSelect')
  await page.getByRole('textbox', { name: 'Choice 1' }).fill('Email')
  await noSeriousViolations(page, 'ticket field dialog')
  await page.getByRole('button', { name: 'Save field' }).click()
  await expect(page.getByText('Which system?')).toBeVisible()
  await page.getByRole('tab', { name: 'Ticket types' }).click()
  await page.getByRole('button', { name: 'New ticket type' }).click()
  await page.getByRole('dialog').getByLabel('Name').fill('Access request')
  await noSeriousViolations(page, 'ticket type dialog')
  await page.getByRole('button', { name: 'Save type' }).click()
  await expect(page.getByText('Access request')).toBeVisible()
  await page.getByRole('button', { name: 'Fields' }).click()
  await page.getByRole('dialog').getByLabel('Add a field').selectOption({ label: 'Which system?' })
  await noSeriousViolations(page, 'type fields dialog')
  await page.getByRole('button', { name: 'Save fields' }).click()
  await expect(page.getByText(/1 field: Which system\?/)).toBeVisible()
  await page.getByRole('tab', { name: 'Request forms' }).click()
  await page.getByRole('button', { name: 'New request form' }).click()
  await page.getByRole('dialog').getByLabel('Name').fill('New access')
  await page.getByRole('dialog').getByLabel('Ticket type').selectOption({ label: 'Access request' })
  await noSeriousViolations(page, 'request form dialog')
  await page.getByRole('button', { name: 'Save form' }).click()
  await expect(page.getByText(/Type Access request/)).toBeVisible()

  await page.getByRole('tab', { name: 'Knowledge' }).click()
  await expect(page.getByRole('cell', { name: new RegExp(`Reset your password ${token}`) })).toBeVisible()
  await page.getByRole('button', { name: 'New article' }).click()
  await noSeriousViolations(page, 'article editor')
  await page.getByRole('dialog').getByLabel('Title').fill(`Edited by admin ${token}`)
  await page.getByRole('dialog').getByLabel('Article text').fill('Some **help**.')
  await page.getByRole('button', { name: 'Publish', exact: true }).click()
  await expect(page.getByRole('cell', { name: new RegExp(`Edited by admin ${token}`) })).toBeVisible()
  await admin.dispose()
})

test('a requester cannot open another requester’s request and learns nothing about it', async ({ page }) => {
  const data = await seed()
  const admin = await configure(data)
  const owner = employee()
  const created = await admin.send<{ reference: string }>('POST', `/api/departments/${data.departmentId}/tickets`, {
    subject: 'Owner only subject',
    description: 'Owner only description',
    requester: { email: owner.email, displayName: owner.name },
  })
  // The owner claims the request by signing in; the other employee then tries the reference directly.
  await page.goto(devLoginPath(owner, '/portal/requests'))
  await expect(page.getByRole('link', { name: /Owner only subject/ })).toBeVisible()

  const other = employee()
  await page.goto(devLoginPath(other, `/portal/requests/${created.reference}`))
  await expect(page.getByText(/not found|do not have access/i).first()).toBeVisible()
  await expect(page.getByText('Owner only subject')).toHaveCount(0)
  await expect(page.getByText('Owner only description')).toHaveCount(0)
  await page.goto('/portal/requests')
  await expect(page.getByText('You have no open requests.')).toBeVisible()
  await admin.dispose()
})

test('the portal works on a phone: no horizontal scrolling on the main pages', async ({ page }) => {
  const data = await seed()
  const admin = await configure(data)
  const base = `/api/departments/${data.departmentId}`
  const form = await admin.send<{ id: string }>('POST', `${base}/request-forms`, { name: 'General request' })
  const article = await admin.send<{ id: string; slug: string; version: string }>('POST', `${base}/knowledge/articles`, { title: 'A helpful article', body: 'Text '.repeat(200) })
  await admin.send('POST', `${base}/knowledge/articles/${article.id}/publish`, { version: article.version })
  const emma = employee()
  const created = await admin.send<{ reference: string }>('POST', `${base}/tickets`, {
    subject: 'A request with a rather long subject line that must wrap on a narrow screen without scrolling sideways',
    description: 'x',
    requester: { email: emma.email, displayName: emma.name },
  })

  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto(devLoginPath(emma, '/portal'))
  for (const [name, path] of [
    ['home', '/portal'],
    ['submit', `/portal/submit/${form.id}`],
    ['requests', '/portal/requests'],
    ['request', `/portal/requests/${created.reference}`],
    ['article', `/portal/knowledge/${article.slug}`],
  ] as const) {
    await page.goto(path)
    await page.getByRole('main').waitFor()
    await page.waitForTimeout(400)
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
    expect(overflow, `${name} scrolls horizontally by ${overflow}px`).toBeLessThanOrEqual(1)
    await noSeriousViolations(page, `${name} (phone)`)
  }

  await admin.dispose()
})
