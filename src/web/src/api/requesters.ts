import { z } from 'zod'
import { api } from './http'
import { pageSchema, ticketListItemSchema } from './ticketSchemas'

export const requesterProfileSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  email: z.string(),
  firstSeenAt: z.string(),
  lastActivityAt: z.string().nullable(),
  openCount: z.number(),
  resolvedCount: z.number(),
  totalCount: z.number(),
  open: z.array(ticketListItemSchema),
  previous: z.array(ticketListItemSchema),
})
export type RequesterProfile = z.infer<typeof requesterProfileSchema>

export const HISTORY_STATES = ['active', 'resolved', 'all'] as const
export type HistoryState = (typeof HISTORY_STATES)[number]

/** Profile and counts cover only the tickets the caller may read; the server filters, never this client. */
export const getRequester = (id: string) => api('GET', `/api/requesters/${encodeURIComponent(id)}`, { schema: requesterProfileSchema })

export const listRequesterTickets = (id: string, state: HistoryState, cursor: string | null, limit = 20) => {
  const p = new URLSearchParams({ state, limit: String(limit) })
  if (cursor) p.set('cursor', cursor)
  return api('GET', `/api/requesters/${encodeURIComponent(id)}/tickets?${p.toString()}`, { schema: pageSchema(ticketListItemSchema) })
}
