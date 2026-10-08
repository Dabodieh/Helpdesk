import { z } from 'zod'
import { api } from './http'

/** Optional queue columns a user can show and order (Ref and Subject are always shown). */
export const QUEUE_COLUMNS = ['requester', 'status', 'priority', 'sla', 'assignee', 'updated'] as const
export type QueueColumn = (typeof QUEUE_COLUMNS)[number]
export const COLUMN_LABELS: Record<QueueColumn, string> = {
  requester: 'Requester',
  status: 'Status',
  priority: 'Priority',
  sla: 'SLA',
  assignee: 'Assignee',
  updated: 'Updated',
}

export const queuePrefsSchema = z.object({
  columns: z.array(z.string()).nullish(),
  defaultQueue: z.string().nullish(),
  defaultViewId: z.string().nullish(),
})
export type QueuePrefs = z.infer<typeof queuePrefsSchema>
export type QueuePrefsInput = { columns?: string[] | null; defaultQueue?: string | null; defaultViewId?: string | null }

export const getQueuePrefs = () => api('GET', '/api/me/queue-preferences', { schema: queuePrefsSchema })
export const putQueuePrefs = (body: QueuePrefsInput) => api('PUT', '/api/me/queue-preferences', { body, schema: queuePrefsSchema })

export const queueTypeSchema = z.object({ id: z.string(), name: z.string() })
export const listQueueTypes = (departmentId: string) => api('GET', `/api/departments/${departmentId}/queue-types`, { schema: z.array(queueTypeSchema) })
