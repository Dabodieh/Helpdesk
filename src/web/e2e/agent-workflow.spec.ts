import { expect, test } from '@playwright/test'
import { devLoginPath, seed } from './support'

// The properties rail is docked open from 1400 px wide (closed behind the Properties button below that), so use a typical desktop width.
test.use({ viewport: { width: 1440, height: 900 } })

// Critical flow of the agent workspace against the real backend and seeded data (see docs/DEVELOPMENT.md).
test('agent signs in, works a ticket and moves to the next one with the keyboard', async ({ page }) => {
  const data = await seed()
  const ticket = page.getByRole('region', { name: 'Ticket' })

  // Sign in through dev-login: agents land on the dashboard (11f0f6b); My Queue is behind Tickets in the main navigation.
  await page.goto(devLoginPath(data.actor))
  await expect(page).toHaveURL(/\/dashboard\//)
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Tickets' }).click()
  await expect(page).toHaveURL(/\/queue\/my/)
  await expect(page.getByRole('heading', { name: 'My Queue', level: 1 })).toBeVisible()

  // Open the first ticket and read the conversation.
  await page.getByRole('link', { name: data.first.subject }).click()
  await expect(page).toHaveURL(new RegExp(`/tickets/${data.first.reference}`))
  await expect(ticket.getByRole('heading', { name: data.first.subject, level: 2 })).toBeVisible()
  await expect(ticket.getByText(data.first.description)).toBeVisible()

  // Priority and status.
  await ticket.getByRole('combobox', { name: 'Priority', exact: true }).selectOption('High')
  await expect(ticket.getByRole('combobox', { name: 'Priority', exact: true })).toHaveValue('High')
  await ticket.getByRole('combobox', { name: 'Status', exact: true }).selectOption('Pending')
  await expect(ticket.getByRole('combobox', { name: 'Status', exact: true })).toHaveValue('Pending')

  // Assignment: to a colleague (the ticket leaves My Queue; Pending keeps it in the default status filter).
  await ticket.getByRole('combobox', { name: 'Assignee', exact: true }).selectOption({ label: data.colleague.name })
  await expect(ticket.getByRole('combobox', { name: 'Assignee', exact: true })).not.toHaveValue('')

  // Internal note: clearly marked, shown in the conversation.
  await page.keyboard.press('n')
  await expect(ticket.getByText('Internal note — not visible to the requester')).toBeVisible()
  await ticket.getByRole('textbox', { name: 'Internal note' }).fill('Checked the fuser, looks worn.')
  await ticket.getByRole('button', { name: 'Add internal note' }).click()
  await expect(ticket.getByRole('article', { name: /^Internal note from/ })).toContainText('Checked the fuser')

  // Public reply: recorded, never claimed as sent.
  await ticket.locator('label', { hasText: 'Public reply' }).click()
  await ticket.getByRole('textbox', { name: 'Public reply' }).fill('We are sending a technician.')
  await ticket.getByRole('button', { name: 'Post public reply' }).click()
  await expect(ticket.getByText('Recorded, not emailed (the ticket’s mailbox is not available to send from)')).toBeVisible()

  // History shows the changes as sentences.
  await ticket.getByRole('button', { name: 'History' }).click()
  await expect(ticket.getByText(/changed priority from Normal to High/)).toBeVisible()
  await ticket.getByRole('button', { name: 'Conversation' }).click()

  // Keyboard: leave the composer and move to the neighbouring ticket of the queue the agent came from ('[' previous, ']' next, on the ticket page). The queue lists the newer ticket first, so the seeded first ticket's neighbour above is the second.
  await page.keyboard.press('Escape')
  // A focused button (the Conversation tab) would swallow the shortcut natively: drop focus first, as a user pressing Esc in the page would.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
  await page.keyboard.press('[')
  await expect(page).toHaveURL(new RegExp(`/tickets/${data.second.reference}`))
  await expect(ticket.getByRole('heading', { name: data.second.subject, level: 2 })).toBeVisible()
})
