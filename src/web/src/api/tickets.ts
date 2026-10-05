import { z } from 'zod'
import { api } from './http'
import {
  activitySchema,
  bulkResultSchema,
  messageSchema,
  pageSchema,
  postMessageResultSchema,
  queueCountsSchema,
  requesterLookupSchema,
  ticketDetailSchema,
  ticketListItemSchema,
  type Priority,
  type Queue,
  type Sort,
  type Status,
} from './ticketSchemas'

export type TicketFilters = {
  queue: Queue
  statuses: Status[]
  priorities: Priority[]
  departmentId?: string
  teamId?: string
  assigneeUserId?: string
  unassigned?: boolean
  q?: string
  sort: Sort
}

export function ticketListPath(f: TicketFilters, cursor: string | null, limit = 50) {
  const p = new URLSearchParams()
  p.set('queue', f.queue)
  for (const s of f.statuses) p.append('status', s)
  for (const s of f.priorities) p.append('priority', s)
  if (f.departmentId) p.set('departmentId', f.departmentId)
  if (f.teamId) p.set('teamId', f.teamId)
  if (f.assigneeUserId) p.set('assigneeUserId', f.assigneeUserId)
  if (f.unassigned) p.set('unassigned', 'true')
  if (f.q) p.set('q', f.q)
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

export const setAssignment = (
  ticket: string,
  body: { version: string; teamId: string | null; assigneeUserId: string | null },
) => api('PUT', `${tk(ticket)}/assignment`, { body, schema: ticketDetailSchema })

const cursorPath = (base: string, cursor: string | null, limit: number) => {
  const p = new URLSearchParams()
  if (cursor) p.set('cursor', cursor)
  p.set('limit', String(limit))
  return `${base}?${p.toString()}`
}

export const listMessages = (ticket: string, cursor: string | null, limit = 50) =>
  api('GET', cursorPath(`${tk(ticket)}/messages`, cursor, limit), { schema: pageSchema(messageSchema) })

export const postMessage = (ticket: string, body: { kind: 'Public' | 'Internal'; body: string; status?: string }) =>
  api('POST', `${tk(ticket)}/messages`, { body, schema: postMessageResultSchema })

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
  teamId?: string
  assigneeUserId?: string
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
  team?: { id: string | null }
  assignee?: { id: string | null }
}

export const bulkUpdate = (ticketIds: string[], change: BulkChange) =>
  api('POST', '/api/tickets/bulk', { body: { ticketIds, ...change }, schema: bulkResultSchema })
