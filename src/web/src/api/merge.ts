import { z } from 'zod'
import { api } from './http'
import { ticketDetailSchema } from './ticketSchemas'

const tk = (ticket: string) => `/api/tickets/${encodeURIComponent(ticket)}`

export const linkCandidateSchema = z.object({
  id: z.string(),
  reference: z.string(),
  subject: z.string(),
  status: z.string(),
  requesterName: z.string(),
  requesterEmail: z.string(),
  version: z.string(),
})
export type LinkCandidate = z.infer<typeof linkCandidateSchema>

/** Tickets of the same department that can be merged or linked (server-side search; inaccessible and merged tickets never appear). */
export const searchCandidates = (ticket: string, q: string) =>
  api('GET', `${tk(ticket)}/link-candidates?q=${encodeURIComponent(q)}`, { schema: z.array(linkCandidateSchema) })

export type MergeSource = { ticketId: string; version: string }

/** The ticket in the route is the CANONICAL one; every source is merged into it. */
export const mergeTickets = (canonical: string, body: { version: string; sources: MergeSource[] }) =>
  api('POST', `${tk(canonical)}/merge`, { body, schema: ticketDetailSchema })

export const LINK_TYPES = ['Related', 'DuplicateOf', 'Parent', 'Child'] as const
export type LinkType = (typeof LINK_TYPES)[number]

/** Wording of a relationship as seen from the ticket that is open. */
export const RELATIONSHIP_LABEL: Record<string, string> = {
  Related: 'Related',
  DuplicateOf: 'Duplicate of',
  DuplicatedBy: 'Has duplicate',
  Parent: 'Parent',
  Child: 'Child',
}

/** Choice wording when adding a link ("the ticket I pick is ..."). */
export const LINK_TYPE_CHOICE: Record<LinkType, string> = {
  Related: 'Related to this ticket',
  DuplicateOf: 'The original of this ticket (this ticket is a duplicate of it)',
  Parent: 'The parent of this ticket',
  Child: 'A child of this ticket',
}

export const ticketLinkSchema = z.object({
  id: z.string(),
  relationship: z.string(),
  ticketId: z.string(),
  reference: z.string(),
  subject: z.string(),
  status: z.string(),
  mergedInto: z.string().nullish(),
})
export type TicketLink = z.infer<typeof ticketLinkSchema>

export const listLinks = (ticket: string) => api('GET', `${tk(ticket)}/links`, { schema: z.array(ticketLinkSchema) })

export const addLink = (ticket: string, body: { ticketId: string; type: LinkType }) =>
  api('POST', `${tk(ticket)}/links`, { body, schema: ticketLinkSchema })

export const removeLink = (ticket: string, linkId: string) => api('DELETE', `${tk(ticket)}/links/${encodeURIComponent(linkId)}`)
