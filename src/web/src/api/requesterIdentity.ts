import { z } from 'zod'
import { api } from './http'

const accountSchema = z.object({ userId: z.string(), displayName: z.string(), email: z.string().nullish() })
export type AccountRef = z.infer<typeof accountSchema>

export const identityCaseSchema = z.object({
  requesterId: z.string(),
  displayName: z.string(),
  email: z.string(),
  ticketCount: z.number(),
  openCount: z.number(),
  firstSeenAt: z.string(),
  lastActivityAt: z.string().nullish(),
  matchingAccount: accountSchema.nullish(),
  needsAttention: z.boolean(),
})
export type IdentityCase = z.infer<typeof identityCaseSchema>

export const identityDetailSchema = z.object({
  case: identityCaseSchema,
  status: z.enum(['Unlinked', 'Linked']),
  linkedAccount: accountSchema.nullish(),
  tickets: z.array(z.object({ reference: z.string(), subject: z.string(), status: z.string(), updatedAt: z.string() })),
})

const base = (d: string) => `/api/departments/${d}/requester-identities`
export const listIdentityCases = (d: string) => api('GET', base(d), { schema: z.array(identityCaseSchema) })
export const getIdentityCase = (d: string, requesterId: string) => api('GET', `${base(d)}/${requesterId}`, { schema: identityDetailSchema })
export const linkRequester = (d: string, requesterId: string, userId: string) =>
  api('POST', `${base(d)}/${requesterId}/link`, { body: { userId }, schema: z.object({ alreadyLinked: z.boolean() }) })
