import { expect, test } from '@playwright/test'
import { Api, devLoginPath, type TestUser } from './support'

// The email loop (docs/product/MVP.md) through the real stack with the in-process mailbox simulator (Mail:Provider=Fake, Development):
// an email arrives -> a ticket appears in the department's queue -> the agent replies -> it is sent from the department mailbox ->
// the requester answers the mail we sent -> the answer is threaded onto the SAME ticket.
test('email in, reply out from the department mailbox, requester answer threads onto the same ticket', async ({ page }) => {
  const run = Date.now().toString(36)
  const key = `M${run.toUpperCase().slice(-6)}`
  const agent: TestUser = { subject: `e2e-mail-agent-${run}`, name: `Mail Agent ${run}`, email: `mail-agent-${run}@example.test` }
  const address = `desk-${run}@helpdesk.test`
  const requester = `customer-${run}@client.test`
  const subject = `E2E scanner offline ${run}`

  const agentApi = await Api.signIn(agent)
  const agentId = (await agentApi.get<{ id: string }>('/api/me')).id
  await agentApi.dispose()

  const admin = await Api.signIn({ subject: 'dev-admin', name: 'Dev Admin', email: 'dev@example.test' })
  const dept = await admin.send<{ id: string }>('POST', '/api/departments', { key, name: `E2E Mail ${run}` })
  await admin.send('PUT', `/api/departments/${dept.id}/members/${agentId}`, { role: 'Agent' })
  await admin.send('POST', `/api/departments/${dept.id}/mailboxes`, { address, displayName: 'E2E Desk', signature: 'Regards,\nE2E Desk' })
  await admin.send('POST', '/api/dev/mail/inbound', {
    mailbox: address,
    from: requester,
    fromName: 'Casey Customer',
    subject,
    bodyText: 'The scanner on floor 2 is offline again.',
    attachments: [{ name: 'photo.png', contentBase64: Buffer.from([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]).toString('base64'), contentType: 'image/png', isInline: false }],
  })

  // The ticket is in the department queue, with its attachment.
  await page.goto(devLoginPath(agent, '/queue/all'))
  await page.getByRole('link', { name: subject }).click()
  const ticket = page.getByRole('region', { name: 'Ticket' })
  await expect(ticket.getByText('The scanner on floor 2 is offline again.')).toBeVisible()
  await expect(ticket.getByRole('link', { name: 'photo.png' })).toBeVisible()
  await expect(ticket.getByText(address)).toBeVisible()

  // Reply: it is sent from the department's mailbox, and the page says so only once the provider accepted it.
  await ticket.getByRole('textbox', { name: 'Public reply' }).fill('We have booked an engineer for tomorrow.')
  await ticket.getByRole('button', { name: 'Post public reply' }).click()
  await expect(ticket.getByText('Sent by email from the ticket’s mailbox (accepted for delivery; not confirmed as delivered)')).toBeVisible({ timeout: 20_000 })
  const sent = await admin.get<{ draftId: string; to: string[]; bodyHtml: string }[]>(`/api/dev/mail/sent?mailbox=${encodeURIComponent(address)}`)
  expect(sent).toHaveLength(1)
  expect(sent[0]!.to).toEqual([requester])
  expect(sent[0]!.bodyHtml).toContain('Regards')

  // The requester answers the mail we sent: standard headers thread it onto the same ticket (no new ticket appears).
  await admin.send('POST', '/api/dev/mail/customer-reply', { mailbox: address, draftId: sent[0]!.draftId, from: requester, bodyText: 'Thanks, tomorrow is perfect.' })
  await page.reload()
  await expect(ticket.getByText('Thanks, tomorrow is perfect.')).toBeVisible({ timeout: 20_000 })
  await page.goto('/queue/all')
  await expect(page.getByRole('link', { name: subject })).toHaveCount(1)

  await admin.dispose()
})
