import { z } from 'zod'
import { api } from './http'

const mailboxRow = z.object({
  id: z.string(),
  departmentId: z.string(),
  departmentName: z.string(),
  address: z.string(),
  displayName: z.string(),
  isActive: z.boolean(),
  isDefault: z.boolean(),
  health: z.string(),
  reasons: z.array(z.string()),
  subscriptionState: z.string(),
  subscriptionExpiresAt: z.string().nullable(),
  lastSyncAt: z.string().nullable(),
  lastSyncFailed: z.boolean(),
  outboundFailed: z.number(),
  outboundUncertain: z.number(),
})
export type M365Mailbox = z.infer<typeof mailboxRow>

const provisioning = z.object({ available: z.boolean(), reason: z.string() })

export const microsoft365Schema = z.object({
  provider: z.string(),
  simulated: z.boolean(),
  graph: z.object({ configured: z.boolean(), tenantId: z.string().nullable(), clientId: z.string().nullable(), credential: z.string(), baseUrl: z.string() }),
  signIn: z.object({ entraConfigured: z.boolean(), tenantId: z.string().nullable(), clientId: z.string().nullable(), secretPresent: z.boolean(), devSignInEnabled: z.boolean(), redirectPath: z.string() }),
  notifications: z.object({ mode: z.string(), baseUrl: z.string().nullable(), publicHttps: z.boolean(), reconcileMinutes: z.number(), subscriptionLifetimeDays: z.number() }),
  requiredPermissions: z.array(z.object({ name: z.string(), purpose: z.string(), scope: z.string(), required: z.boolean() })),
  mailboxes: z.array(mailboxRow),
  issues: z.array(z.string()),
  provisioning,
  approvedDomains: z.array(z.string()),
})
export type Microsoft365Status = z.infer<typeof microsoft365Schema>

export const connectionCheckSchema = z.object({ ok: z.boolean(), code: z.string(), checkedAt: z.string() })
export type ConnectionCheck = z.infer<typeof connectionCheckSchema>

export const mailboxCheckSchema = z.object({
  address: z.string(),
  valid: z.boolean(),
  problem: z.string().nullable(),
  registeredDepartmentId: z.string().nullable(),
  registeredDepartmentName: z.string().nullable(),
  access: z.enum(['ok', 'denied', 'unavailable', 'skipped']),
})
export type MailboxCheck = z.infer<typeof mailboxCheckSchema>

export const newMailboxPlanSchema = z.object({
  valid: z.boolean(),
  problems: z.record(z.string(), z.string()),
  address: z.string().nullable(),
  departmentName: z.string().nullable(),
  script: z.array(z.string()),
  provisioning,
})
export type NewMailboxPlan = z.infer<typeof newMailboxPlanSchema>

export const getMicrosoft365 = () => api('GET', '/api/admin/microsoft-365', { schema: microsoft365Schema })

/** Read-only: asks only whether the application can sign in. */
export const checkConnection = () => api('POST', '/api/admin/microsoft-365/check-connection', { body: {}, schema: connectionCheckSchema })

/** Read-only: validates the address, looks for a duplicate and probes access. Registers nothing. */
export const checkMailbox = (address: string) => api('POST', '/api/admin/microsoft-365/check-mailbox', { body: { address }, schema: mailboxCheckSchema })

/** Validates a new shared mailbox request and returns the script for a tenant administrator. Creates nothing. */
export const planNewMailbox = (body: { displayName: string; alias: string; domain: string; departmentId: string }) =>
  api('POST', '/api/admin/microsoft-365/new-mailbox-plan', { body, schema: newMailboxPlanSchema })
