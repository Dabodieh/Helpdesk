import { z } from 'zod'

export const STATUSES = ['Open', 'Pending', 'Resolved', 'Closed'] as const
export const PRIORITIES = ['Low', 'Normal', 'High', 'Urgent'] as const
export type Status = (typeof STATUSES)[number]
export type Priority = (typeof PRIORITIES)[number]
export const QUEUES = ['my', 'team', 'unassigned', 'all'] as const
export type Queue = (typeof QUEUES)[number]
export const SORTS = ['updated_desc', 'updated_asc', 'created_desc', 'created_asc', 'priority_desc'] as const
export type Sort = (typeof SORTS)[number]

const statusSchema = z.enum(STATUSES)
const prioritySchema = z.enum(PRIORITIES)
const refSchema = z.object({ id: z.string(), displayName: z.string() })
const nameRefSchema = z.object({ id: z.string(), name: z.string() })

export const matchSchema = z.object({ field: z.string(), snippet: z.string().nullish() })

export const ticketListItemSchema = z.object({
  id: z.string(),
  reference: z.string(),
  departmentId: z.string(),
  departmentKey: z.string(),
  subject: z.string(),
  status: statusSchema,
  priority: prioritySchema,
  requester: z.object({ id: z.string(), displayName: z.string(), email: z.string() }),
  team: nameRefSchema.nullable(),
  assignee: refSchema.nullable(),
  createdAt: z.string(),
  updatedAt: z.string(),
  /** Present for searches: why the ticket matched (never HTML). */
  match: matchSchema.nullish(),
})
export type TicketListItem = z.infer<typeof ticketListItemSchema>

export const mailboxRefSchema = z.object({ id: z.string(), address: z.string(), name: z.string() })
export const participantSchema = z.object({ email: z.string(), displayName: z.string().nullable().optional() })

export const ticketDetailSchema = ticketListItemSchema.extend({
  departmentName: z.string(),
  resolvedAt: z.string().nullable(),
  closedAt: z.string().nullable(),
  version: z.string(),
  requester: z.object({
    id: z.string(),
    displayName: z.string(),
    email: z.string(),
    openTicketCount: z.number(),
    totalTicketCount: z.number(),
  }),
  mailbox: mailboxRefSchema.nullable().optional(),
  participants: z.array(participantSchema).default([]),
})
export type TicketDetail = z.infer<typeof ticketDetailSchema>

export const attachmentSchema = z.object({
  id: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  size: z.number(),
  isInline: z.boolean(),
  state: z.enum(['Stored', 'Blocked']),
  blockReason: z.string().nullable().optional(),
  scanState: z.string(),
  downloadable: z.boolean(),
})
export type Attachment = z.infer<typeof attachmentSchema>

export const DELIVERY_STATES = ['None', 'NotDelivered', 'Pending', 'Sent', 'Failed'] as const

export const messageSchema = z.object({
  id: z.string(),
  ticketId: z.string(),
  kind: z.enum(['Public', 'Internal']),
  authorType: z.enum(['Agent', 'Requester', 'System']),
  author: z.object({ id: z.string().nullable(), displayName: z.string() }),
  body: z.string(),
  /** Sanitised HTML of an email body (allow-list; never rendered inline in the page, only inside a sandboxed frame). */
  bodyHtml: z.string().nullable().optional(),
  source: z.string(),
  deliveryState: z.enum(DELIVERY_STATES),
  deliveryError: z.string().nullable().optional(),
  attachments: z.array(attachmentSchema).default([]),
  createdAt: z.string(),
})
export type Message = z.infer<typeof messageSchema>

export const activitySchema = z.object({
  id: z.string(),
  occurredAt: z.string(),
  actor: refSchema.nullable(),
  action: z.string(),
  previous: z.record(z.string(), z.unknown()).nullable().optional(),
  next: z.record(z.string(), z.unknown()).nullable().optional(),
  reason: z.string().nullable().optional(),
})
export type Activity = z.infer<typeof activitySchema>

export const pageSchema = <T extends z.ZodType>(item: T) =>
  z.object({ items: z.array(item), nextCursor: z.string().nullable() })

export const queueCountsSchema = z.object({ my: z.number(), team: z.number(), unassigned: z.number(), all: z.number() })
export type QueueCounts = z.infer<typeof queueCountsSchema>

export const requesterLookupSchema = z.object({ id: z.string(), displayName: z.string(), email: z.string() })
export type RequesterLookup = z.infer<typeof requesterLookupSchema>

export const postMessageResultSchema = z.object({ message: messageSchema, ticket: ticketDetailSchema })

export const bulkResultSchema = z.object({
  results: z.array(z.object({ ticketId: z.string(), reference: z.string().nullish(), outcome: z.string(), code: z.string().nullish() })),
  updated: z.number(),
  unchanged: z.number(),
  failed: z.number(),
})
export type BulkResult = z.infer<typeof bulkResultSchema>
