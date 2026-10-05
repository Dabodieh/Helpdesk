import { z } from 'zod'
import { api } from './http'

export const viewFilterSchema = z.object({
  queue: z.string().nullish(),
  departmentId: z.string().nullish(),
  teamId: z.string().nullish(),
  assigneeUserId: z.string().nullish(),
  unassigned: z.boolean().nullish(),
  status: z.array(z.string()).nullish(),
  priority: z.array(z.string()).nullish(),
  q: z.string().nullish(),
  sort: z.string().nullish(),
})
export type ViewFilter = z.infer<typeof viewFilterSchema>

export const savedViewSchema = z.object({
  id: z.string(),
  name: z.string(),
  filter: viewFilterSchema,
  isPinned: z.boolean(),
  version: z.string(),
})
export type SavedView = z.infer<typeof savedViewSchema>

export const listViews = () => api('GET', '/api/views', { schema: z.array(savedViewSchema) })

export const createView = (body: { name: string; filter: ViewFilter; isPinned?: boolean }) =>
  api('POST', '/api/views', { body, schema: savedViewSchema })

export const updateView = (id: string, body: { version: string; name?: string; filter?: ViewFilter; isPinned?: boolean }) =>
  api('PATCH', `/api/views/${encodeURIComponent(id)}`, { body, schema: savedViewSchema })

export const deleteView = (id: string) => api('DELETE', `/api/views/${encodeURIComponent(id)}`)
