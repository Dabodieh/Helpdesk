import { z } from 'zod'
import { api } from './http'
import {
  activitySchema,
  bulkResultSchema,
  messageSchema,
  pageSchema,
  participantSchema,
  postMessageResultSchema,
  queueCountsSchema,
  requesterLookupSchema,
  ticketDetailSchema,
  ticketListItemSchema,
  type Priority,
  type Queue,
  type Sort,
  type SendFilter,
  type SlaFilter,
  type Status,
} from './ticketSchemas'

export type TicketFilters = {
  queue: Queue
  statuses: Status[]
  priorities: Priority[]
  departmentId?: string
  assigneeUserId?: string
  unassigned?: boolean
  q?: string
  sla?: SlaFilter
  typeId?: string
  send?: SendFilter
  sort: Sort
}

export function ticketListPath(f: TicketFilters, cursor: string | null, limit = 50) {
  const p = new URLSearchParams()
  p.set('queue', f.queue)
  for (const s of f.statuses) p.append('status', s)
  for (const s of f.priorities) p.append('priority', s)
  if (f.departmentId) p.set('departmentId', f.departmentId)
  if (f.assigneeUserId) p.set('assigneeUserId', f.assigneeUserId)
  if (f.unassigned) p.set('unassigned', 'true')
  if (f.q) p.set('q', f.q)
  if (f.sla) p.set('sla', f.sla)
  if (f.typeId) p.set('typeId', f.typeId)
  if (f.send) p.set('send', f.send)
  p.set('sort', f.sort)
  if (cursor) p.set('cursor', cursor)
  p.set('limit', String(limit))
  return `/api/tickets?${p.toString()}`
}

export const listTickets = (f: TicketFilters, cursor: string | null) =>
  api('GET', ticketListPath(f, cursor), { schema: pageSchema(ticketListItemSchema) })

export const getQueueCounts = () => api('GET', '/api/tickets/queue-counts', { schema: queueCountsSchema })

const tk = (ticket: string) => `/api/tickets/${encodeURIComponent(ticket)}`

export const getTicket = (ticket: string) => api('GET', tk(ticket), { schema: ticketDetailSchema })

export const updateTicket = (
  ticket: string,
  body: { version: string; subject?: string; status?: string; priority?: string },
) => api('PATCH', tk(ticket), { body, schema: ticketDetailSchema })

/** D22: explicit assignment (or pre-send correction) of the ticket's mailbox. */
export const assignTicketMailbox = (ticket: string, version: string, mailboxId: string) =>
  api('POST', `${tk(ticket)}/mailbox`, { body: { version, mailboxId }, schema: ticketDetailSchema })

export const setAssignment = (
  ticket: string,
  body: { version: string; assigneeUserId: string | null },
) => api('PUT', `${tk(ticket)}/assignment`, { body, schema: ticketDetailSchema })

const cursorPath = (base: string, cursor: string | null, limit: number) => {
  const p = new URLSearchParams()
  if (cursor) p.set('cursor', cursor)
  p.set('limit', String(limit))
  return `${base}?${p.toString()}`
}

export const listMessages = (ticket: string, cursor: string | null, limit = 50) =>
  api('GET', cursorPath(`${tk(ticket)}/messages`, cursor, limit), { schema: pageSchema(messageSchema) })

/** `clientMessageId` (a GUID made once per composed message) lets the server recognise a repeated post and return the first result. */
export const postMessage = (ticket: string, body: { kind: 'Public' | 'Internal'; body: string; status?: string; mentionUserIds?: string[]; clientMessageId?: string }) =>
  api('POST', `${tk(ticket)}/messages`, { body, schema: postMessageResultSchema })

const inlineImageSchema = z.object({ id: z.string(), url: z.string(), contentType: z.string(), size: z.number() })

/** Whether the composer may offer image pasting: the server-side attachment switch, never a client decision. */
export const getTicketCapabilities = () =>
  api('GET', '/api/ticket-capabilities', { schema: z.object({ attachmentsEnabled: z.boolean(), maxInlineImageBytes: z.number() }) })

/** Uploads a pasted image as raw bytes. It stays pending (usable only by its author on this ticket) until a message that names it is posted. */
export const uploadInlineImage = (ticket: string, image: Blob) => api('POST', `${tk(ticket)}/inline-images`, { raw: image, schema: inlineImageSchema })

/** Eligible colleagues for an @mention in an internal note of this ticket (this department only; the server decides eligibility). */
export const searchMentionable = (ticket: string, q: string) =>
  api('GET', `${tk(ticket)}/mentionable?q=${encodeURIComponent(q)}`, { schema: z.array(z.object({ userId: z.string(), displayName: z.string() })) })

/** Takes an ordinary participant off future Cc (final for the ticket; history is untouched). The address travels in the body, never the URL. */
export const removeParticipant = (ticket: string, email: string) =>
  api('POST', `${tk(ticket)}/participants/remove`, { body: { email }, schema: z.array(participantSchema) })

export const retryDelivery = (ticket: string, messageId: string) =>
  api('POST', `${tk(ticket)}/messages/${encodeURIComponent(messageId)}/retry-delivery`, { schema: messageSchema })

/** Attachments are always downloaded (Content-Disposition: attachment), never rendered by the page. */
export const attachmentHref = (ticketId: string, attachmentId: string) =>
  `/api/tickets/${encodeURIComponent(ticketId)}/attachments/${encodeURIComponent(attachmentId)}`

export const listActivity = (ticket: string, cursor: string | null, limit = 50) =>
  api('GET', cursorPath(`${tk(ticket)}/activity`, cursor, limit), { schema: pageSchema(activitySchema) })

export type NewTicket = {
  subject: string
  description: string
  requester: { email: string; displayName?: string } | { requesterId: string }
  priority?: Priority
  assigneeUserId?: string
  mailboxId?: string
  ticketTypeId?: string
}

export function createTicket(departmentId: string, input: NewTicket) {
  const { requester, ...rest } = input
  const body = 'requesterId' in requester ? { ...rest, requesterId: requester.requesterId } : { ...rest, requester }
  return api('POST', `/api/departments/${encodeURIComponent(departmentId)}/tickets`, {
    body,
    schema: ticketDetailSchema,
  })
}

export const lookupRequesters = (departmentId: string, q: string) =>
  api('GET', `/api/departments/${encodeURIComponent(departmentId)}/requesters/lookup?q=${encodeURIComponent(q)}`, {
    schema: z.array(requesterLookupSchema),
  })

export type BulkChange = {
  status?: string
  priority?: string
  assignee?: { id: string | null }
}

/** `versions` (ticket id to the version seen when selected): a ticket that changed since is reported as changed and not touched. */
export const bulkUpdate = (ticketIds: string[], change: BulkChange, versions?: Record<string, string>) =>
  api('POST', '/api/tickets/bulk', { body: { ticketIds, ...change, versions }, schema: bulkResultSchema })

/** Knowledge articles this ticket's requester can open (published, their audience, this department only); empty when the requester has no linked account. */
export const articleLinkSchema = z.object({ slug: z.string(), title: z.string(), category: z.string().nullish(), path: z.string() })
export type ArticleLink = z.infer<typeof articleLinkSchema>
export const searchArticleLinks = (ticket: string, q: string) => api('GET', `${tk(ticket)}/article-links?q=${encodeURIComponent(q)}`, { schema: z.array(articleLinkSchema) })
