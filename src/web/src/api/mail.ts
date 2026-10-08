import { z } from 'zod'
import { api } from './http'

export const mailboxSchema = z.object({
  id: z.string(),
  departmentId: z.string(),
  address: z.string(),
  displayName: z.string(),
  provider: z.string(),
  isActive: z.boolean(),
  isDefault: z.boolean(),
  version: z.string(),
  subscription: z.object({ state: z.string(), expiresAt: z.string().nullable() }),
  lastSyncAt: z.string().nullable(),
  lastSyncError: z.string().nullable(),
  attentionCount: z.number(),
  health: z.object({ status: z.enum(['Healthy', 'Attention', 'Failed', 'NotConfigured', 'Disabled']), reasons: z.array(z.string()) }).optional(),
  lastSendAt: z.string().nullish(),
  outboundFailedCount: z.number().optional(),
  outboundUncertainCount: z.number().optional(),
})
export type Mailbox = z.infer<typeof mailboxSchema>

export const attentionItemSchema = z.object({
  id: z.string(),
  mailboxId: z.string(),
  direction: z.enum(['Inbound', 'Outbound']),
  state: z.enum(['Failed', 'Uncertain', 'Ignored']),
  ignoreReason: z.string().nullable(),
  occurredAt: z.string(),
  address: z.string(),
  subject: z.string(),
  lastError: z.string().nullable(),
  attempts: z.number(),
  ticketId: z.string().nullable(),
  category: z.string().nullish(),
})
export type AttentionItem = z.infer<typeof attentionItemSchema>

const attentionPageSchema = z.object({ items: z.array(attentionItemSchema), nextCursor: z.string().nullable() })

const base = (departmentId: string) => `/api/departments/${encodeURIComponent(departmentId)}`

export const listMailboxes = (departmentId: string) =>
  api('GET', `${base(departmentId)}/mailboxes`, { schema: z.array(mailboxSchema) })

export const createMailbox = (
  departmentId: string,
  body: { address: string; displayName: string; isDefault?: boolean },
) => api('POST', `${base(departmentId)}/mailboxes`, { body, schema: mailboxSchema })

export const updateMailbox = (
  departmentId: string,
  mailboxId: string,
  body: { version: string; displayName?: string; isActive?: boolean; isDefault?: boolean },
) => api('PATCH', `${base(departmentId)}/mailboxes/${encodeURIComponent(mailboxId)}`, { body, schema: mailboxSchema })

export const listAttention = (departmentId: string, cursor: string | null) =>
  api('GET', `${base(departmentId)}/mail/attention${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`, { schema: attentionPageSchema })

/** Retry failed mail. For an Uncertain send the server first checks the provider; re-sending anyway needs `acceptDuplicateRisk` (the requester may then get it twice). */
/** D12: turn an ignored message into a ticket (never for delivery reports, loops, duplicates or mail without a sender; the server decides). */
export const releaseMail = (departmentId: string, mailMessageId: string) =>
  api('POST', `${base(departmentId)}/mail/${encodeURIComponent(mailMessageId)}/release`, { schema: z.object({ state: z.string(), ticketId: z.string().nullish() }) })

export const retryMail = (departmentId: string, mailMessageId: string, acceptDuplicateRisk = false) =>
  api('POST', `${base(departmentId)}/mail/${encodeURIComponent(mailMessageId)}/retry`, acceptDuplicateRisk ? { body: { acceptDuplicateRisk: true } } : {})
