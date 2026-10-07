import { z } from 'zod'
import { api } from './http'

const dept = (d: string) => `/api/departments/${encodeURIComponent(d)}`

// ----- the caller's own signature for one department -----

export const signatureSchema = z.object({
  departmentId: z.string(),
  departmentName: z.string(),
  html: z.string().nullish(),
  updatedAt: z.string().nullish(),
  placeholders: z.array(z.string()),
})
export type Signature = z.infer<typeof signatureSchema>

export const getSignature = (d: string) => api('GET', `${dept(d)}/signature/me`, { schema: signatureSchema })
export const saveSignature = (d: string, html: string) => api('PUT', `${dept(d)}/signature/me`, { body: { html }, schema: signatureSchema })
export const previewSignature = (d: string, html: string) => api('POST', `${dept(d)}/signature/me/preview`, { body: { html }, schema: z.object({ html: z.string() }) })

// ----- department acknowledgement of new tickets -----

export const autoReplySchema = z.object({
  departmentId: z.string(),
  configured: z.boolean(),
  enabled: z.boolean(),
  forEmail: z.boolean(),
  forPortal: z.boolean(),
  subject: z.string(),
  body: z.string(),
  placeholders: z.array(z.string()),
  version: z.string(),
})
export type AutoReply = z.infer<typeof autoReplySchema>

export const getAutoReply = (d: string) => api('GET', `${dept(d)}/auto-reply`, { schema: autoReplySchema })
export const saveAutoReply = (d: string, body: { enabled: boolean; forEmail: boolean; forPortal: boolean; subject: string; body: string; version?: string }) =>
  api('PUT', `${dept(d)}/auto-reply`, { body, schema: autoReplySchema })
export const previewAutoReply = (d: string, body: { subject: string; body: string }) =>
  api('POST', `${dept(d)}/auto-reply/preview`, { body, schema: z.object({ subject: z.string(), body: z.string(), fromName: z.string(), fromAddress: z.string() }) })

// ----- mailboxes a hand-made ticket can use -----

export const outboundMailboxSchema = z.object({ id: z.string(), address: z.string(), name: z.string(), isDefault: z.boolean() })
export type OutboundMailbox = z.infer<typeof outboundMailboxSchema>
export const listOutboundMailboxes = (d: string) => api('GET', `${dept(d)}/outbound-mailboxes`, { schema: z.array(outboundMailboxSchema) })
